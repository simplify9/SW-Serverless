using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.UnitTests.Fixtures;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// A classic adapter's startup values hold passwords and keys. On the command line, as they
    /// have always gone, any process on the machine can read them. An adapter built on an SDK
    /// that reads them from stdin gets them there; one built before still gets them as arguments.
    /// </summary>
    [TestClass]
    public class StartupValuesOnStdinTests
    {
        const string Project = "SW.Serverless.UnitTests.Adapter";
        const string Secret = "s3cret-on-stdin-only";

        static IHost host;
        static string workDirectory;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            workDirectory = Path.Combine(Path.GetTempPath(), "swsl-stdin", Guid.NewGuid().ToString("N"));
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-stdin");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(workDirectory, "installed");
                        o.AdapterMetadataCacheDuration = 1;
                        o.CommandTimeout = 30;
                    });
                })
                .Build();
            await host.StartAsync();

            await PublishAsync("test.stdin.new", sdkVersion: "10.1.0");
            await PublishAsync("test.stdin.old", sdkVersion: null);
        }

        static async Task PublishAsync(string adapterId, string sdkVersion)
        {
            var output = TestStore.LocateBuildOutput(Project);
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(output, file), CompressionLevel.Fastest);
                if (sdkVersion != null)
                    using (var writer = new StreamWriter(archive.CreateEntry("adapter.json").Open()))
                        writer.Write(new JObject { ["id"] = adapterId, ["entry"] = Project + ".dll", ["sdkVersion"] = sdkVersion }.ToString());
            }
            buffer.Position = 0;
            await host.Services.GetRequiredService<ICloudFilesService>().WriteAsync(buffer, new WriteFileSettings
            {
                Key = $"adapters/{adapterId}",
                ContentType = "application/zip",
                Metadata = new Dictionary<string, string>
                {
                    ["EntryAssembly"] = Project + ".dll",
                    ["Hash"] = adapterId.Replace('.', '-') + "-" + Guid.NewGuid().ToString("N")[..8],
                }
            });
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) await host.StopAsync();
            host?.Dispose();
            try { Directory.Delete(workDirectory, true); } catch { }
        }

        /// <summary>The command lines of this machine's running adapter processes, as any process can read them.</summary>
        static List<string> AdapterCommandLines()
        {
            using var ps = Process.Start(new ProcessStartInfo("ps", "-axo args") { RedirectStandardOutput = true })!;
            var lines = ps.StandardOutput.ReadToEnd().Split('\n');
            ps.WaitForExit();
            return lines.Where(l => l.Contains(Project + ".dll") && l.Contains(workDirectory)).ToList();
        }

        static async Task<(string Password, List<string> CommandLines)> RunAsync(string adapterId)
        {
            var service = host.Services.GetRequiredService<IServerlessService>();
            await service.StartAsync(adapterId, "corr", new Dictionary<string, string> { ["Password"] = Secret });
            try
            {
                var password = await service.InvokeAsync<string>("TestStartupValue", "Password");
                return (password, AdapterCommandLines());
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }

        [TestMethod]
        public async Task An_adapter_on_the_newer_SDK_gets_its_values_on_stdin_and_none_on_its_command_line()
        {
            if (OperatingSystem.IsWindows()) Assert.Inconclusive("reads command lines with ps");

            var (password, commandLines) = await RunAsync("test.stdin.new");

            Assert.AreEqual(Secret, password, "the value reached the adapter");
            var line = commandLines.Single();
            StringAssert.Contains(line, "--values-on-stdin");
            Assert.AreEqual(3, line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                $"nothing but dotnet, the assembly and the flag on the command line: {line}");
        }

        [TestMethod]
        public async Task An_adapter_from_before_still_gets_its_values_as_arguments()
        {
            if (OperatingSystem.IsWindows()) Assert.Inconclusive("reads command lines with ps");

            var (password, commandLines) = await RunAsync("test.stdin.old");

            Assert.AreEqual(Secret, password);
            var line = commandLines.Single();
            Assert.IsFalse(line.Contains("--values-on-stdin"), line);
            Assert.AreEqual(5, line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                $"dotnet, the assembly and the three base64 values, as before: {line}");
        }
    }
}
