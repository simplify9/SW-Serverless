using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Installer;
using SW.Serverless.Installer.Shared;

namespace SW.Serverless.CompatibilityTests;

/// <summary>A storage folder of its own, so these tests never meet another suite's adapters.</summary>
public sealed class Bucket : IDisposable
{
    public string Name { get; } = "swsl-compat-" + Guid.NewGuid().ToString("N")[..12];
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swsl-compat", Guid.NewGuid().ToString("N"));

    public ServerlessUploadOptions Options => new() { Provider = "local", BucketName = Name, ServiceUrl = Path };

    /// <summary>The installer's own filesystem provider, the same one <c>-p local</c> builds.</summary>
    public ICloudFilesService Files => CloudFilesFactory.Create(Options);

    public string[] Flags => new[] { "-p", "local", "-b", Name, "-u", Path };

    public async Task<List<string>> KeysAsync(string prefix = "") =>
        (await Files.ListAsync(prefix)).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

public static class Compat
{
    public static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find SW.Serverless.sln above the tests.");
        }
    }

    public static string ProjectFile(string project) =>
        System.IO.Path.Combine(RepositoryRoot, project, project + ".csproj");

    /// <summary>A project's build output, built beside the tests through a build-order reference.</summary>
    public static string BuildOutput(string project)
    {
        var bin = System.IO.Path.Combine(RepositoryRoot, project, "bin");
        var found = Directory.Exists(bin)
            ? Directory.EnumerateDirectories(bin, "net*", SearchOption.AllDirectories)
                .Where(d => File.Exists(System.IO.Path.Combine(d, project + ".dll")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(System.IO.Path.Combine(d, project + ".dll")))
                .FirstOrDefault()
            : null;
        return found ?? throw new DirectoryNotFoundException($"Build {project} first — nothing under {bin} contains {project}.dll.");
    }

    /// <summary>A copy of the build output, to publish from: the publisher writes adapter.json into it.</summary>
    public static string CopyOfBuildOutput(string project)
    {
        var source = BuildOutput(project);
        var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swsl-compat", "publish-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return target;
    }

    /// <summary>Publishes a project's build output with the CURRENT installer, without rebuilding it.</summary>
    public static Task<PublishResult> PublishAsync(Bucket bucket, string project, string adapterId, string? version,
        bool promote = true, bool probe = false) =>
        PackagePublisher.PublishAsync(bucket.Files, new PublishRequest
        {
            AdapterId = adapterId,
            ProjectPath = ProjectFile(project),
            PublishPath = CopyOfBuildOutput(project),
            EntryAssembly = project + ".dll",
            WorkPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swsl-compat", "work-" + Guid.NewGuid().ToString("N")),
            Version = version,
            Promote = promote,
            Probe = probe,
            PublishedBy = "compat",
        }, _ => { });

    /// <summary>
    /// A package the way every installer before the manifest made one — a zip with EntryAssembly and
    /// Hash metadata, no adapter.json, no catalog — at <paramref name="key"/>.
    /// </summary>
    public static async Task PublishTheOldWayAsync(ICloudFilesService files, string key, string project,
        IDictionary<string, string>? extraMetadata = null)
    {
        var output = BuildOutput(project);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(file, System.IO.Path.GetRelativePath(output, file), CompressionLevel.Fastest);

        buffer.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(buffer))[..16].ToLowerInvariant();
        buffer.Position = 0;

        var metadata = new Dictionary<string, string>(extraMetadata ?? new Dictionary<string, string>())
        {
            ["EntryAssembly"] = project + ".dll",
            ["Hash"] = hash,
        };
        await files.WriteAsync(buffer, new WriteFileSettings { Key = key, ContentType = "application/zip", Metadata = metadata });
    }

    /// <summary>The current host, from this repository, installing from <paramref name="bucket"/>.</summary>
    public static ServiceProvider NewHost(Bucket bucket) =>
        new ServiceCollection()
            .AddLogging(l => l.ClearProviders())
            // The registration extensions bind their options from configuration and throw without one.
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddLocalTestsCloudFiles(o =>
            {
                o.BucketName = bucket.Name;
                o.StoragePath = bucket.Path;
            })
            .AddServerless(o =>
            {
                o.AdapterRemotePath = "adapters";
                o.AdapterLocalPath = System.IO.Path.Combine(bucket.Path, "..", "installed-" + Guid.NewGuid().ToString("N"));
                o.AdapterMetadataCacheDuration = 1;
                o.CommandTimeout = 60;
            })
            .BuildServiceProvider();

    public record OldHostRun(int ExitCode, List<string> Lines)
    {
        public string? Value(string marker) =>
            Lines.FirstOrDefault(l => l.StartsWith(marker + ":", StringComparison.Ordinal))?[(marker.Length + 1)..];

        public override string ToString() => $"exit {ExitCode}:\n{string.Join("\n", Lines)}";
    }

    /// <summary>Runs the host built on the PUBLISHED SimplyWorks.Serverless 10.0.0 as its own process.</summary>
    /// <summary>Reads a manifest file with the parser published in SimplyWorks.Serverless.Contract 10.0.2.</summary>
    public static async Task<OldHostRun> ReadWithManifestV1002Async(string manifestPath)
    {
        var entry = System.IO.Path.Combine(BuildOutput("SW.Serverless.Compat.ManifestV1002"), "SW.Serverless.Compat.ManifestV1002.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(entry);
        start.ArgumentList.Add(manifestPath);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(true);
            Assert.Fail("the 10.0.2 manifest reader did not finish within a minute");
        }

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines.AddRange((await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "stderr: " + l.TrimEnd('\r')));
        return new OldHostRun(process.ExitCode, lines);
    }

    public static async Task<OldHostRun> RunOldHostAsync(Bucket bucket, string adapter, params string[] args)
    {
        var entry = System.IO.Path.Combine(BuildOutput("SW.Serverless.Compat.OldHost"), "SW.Serverless.Compat.OldHost.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { entry, "--bucket", bucket.Name, "--path", bucket.Path, "--adapter", adapter }.Concat(args))
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(true);
            Assert.Fail("the old host did not finish within two minutes");
        }

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines.AddRange((await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "stderr: " + l.TrimEnd('\r')));
        return new OldHostRun(process.ExitCode, lines);
    }
}
