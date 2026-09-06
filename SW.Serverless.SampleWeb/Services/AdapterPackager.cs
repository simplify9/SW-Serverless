using Microsoft.Extensions.Logging;
using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SW.Serverless.SampleWeb.Services
{
    /// <summary>
    /// Does what SW.Serverless.Installer does, in-process, so the sample can demonstrate the
    /// full loop with no cloud account: zip the build output, hash it, upload it with the
    /// metadata the installer reads back — EntryAssembly, Hash, and the protocol-2 keys that
    /// tell the host what runtime shape this adapter wants.
    /// </summary>
    public class AdapterPackager
    {
        readonly ICloudFilesService cloudFiles;
        readonly ServerlessOptions options;
        readonly ILogger<AdapterPackager> logger;

        public AdapterPackager(ICloudFilesService cloudFiles, ServerlessOptions options,
            ILogger<AdapterPackager> logger)
        {
            this.cloudFiles = cloudFiles;
            this.options = options;
            this.logger = logger;
        }

        public async Task<string> PublishAsync(string adapterId, string projectName,
            IDictionary<string, string> adapterValues)
        {
            var output = LocateBuildOutput(projectName);
            var entryAssembly = projectName + ".dll";

            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(output, file);
                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }

            buffer.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(buffer))[..16].ToLowerInvariant();
            buffer.Position = 0;

            var metadata = new Dictionary<string, string>(adapterValues ?? new Dictionary<string, string>())
            {
                ["EntryAssembly"] = entryAssembly,
                ["Hash"] = hash
            };

            await cloudFiles.WriteAsync(buffer, new WriteFileSettings
            {
                Key = $"{options.AdapterRemotePath}/{adapterId}".ToLower(),
                ContentType = "application/zip",
                Metadata = metadata
            });

            logger.LogInformation("Packaged {AdapterId} ({Bytes} KB, hash {Hash}) into the local store.",
                adapterId, buffer.Length / 1024, hash);

            return hash;
        }

        static string LocateBuildOutput(string projectName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, projectName))) dir = dir.Parent;
            if (dir == null) throw new DirectoryNotFoundException($"Could not find the {projectName} project.");

            var root = Path.Combine(dir.FullName, projectName, "bin");
            var candidate = Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, "net*", SearchOption.AllDirectories)
                    .Where(d => File.Exists(Path.Combine(d, projectName + ".dll")))
                    .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, projectName + ".dll")))
                    .FirstOrDefault()
                : null;

            return candidate ?? throw new DirectoryNotFoundException(
                $"Build {projectName} first — no output containing {projectName}.dll under {root}.");
        }
    }
}
