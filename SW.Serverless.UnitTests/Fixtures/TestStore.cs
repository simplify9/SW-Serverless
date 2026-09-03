using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests.Fixtures
{
    /// <summary>
    /// Packages an adapter's build output into the local-filesystem cloud store, with the same
    /// metadata the installer reads back. This is what lets the tests cover installation for
    /// real rather than pointing at a pre-seeded bucket.
    /// </summary>
    public static class TestStore
    {
        public const string BucketName = "sw-serverless-unittests";

        public static async Task PublishAsync(ICloudFilesService cloudFiles, string adapterId,
            string projectName, IDictionary<string, string> adapterValues = null)
        {
            var output = LocateBuildOutput(projectName);

            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(output, file),
                        CompressionLevel.Fastest);

            buffer.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(buffer))[..16].ToLowerInvariant();
            buffer.Position = 0;

            var metadata = new Dictionary<string, string>(adapterValues ?? new Dictionary<string, string>())
            {
                ["EntryAssembly"] = projectName + ".dll",
                ["Hash"] = hash
            };

            await cloudFiles.WriteAsync(buffer, new WriteFileSettings
            {
                Key = $"adapters/{adapterId}".ToLower(),
                ContentType = "application/zip",
                Metadata = metadata
            });
        }

        public static string LocateBuildOutput(string projectName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, projectName)))
                dir = dir.Parent;
            if (dir == null)
                throw new DirectoryNotFoundException($"Could not find the {projectName} project.");

            var root = Path.Combine(dir.FullName, projectName, "bin");
            var candidate = Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, "net*", SearchOption.AllDirectories)
                    .Where(d => File.Exists(Path.Combine(d, projectName + ".dll")))
                    .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, projectName + ".dll")))
                    .FirstOrDefault()
                : null;

            return candidate ?? throw new DirectoryNotFoundException(
                $"Build {projectName} first — nothing under {root} contains {projectName}.dll.");
        }

        public static string EntryAssembly(string projectName) =>
            Path.Combine(LocateBuildOutput(projectName), projectName + ".dll");
    }
}
