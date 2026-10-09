using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Resident;
using SW.Serverless.Runtimes;
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
    /// The exec runtime: an adapter that is one self-contained executable, started as itself rather
    /// than through dotnet or an interpreter. Built here as the Greedy sample published single-file
    /// for this machine, packaged with an exec manifest the way any language's binary would be, and
    /// run by the resident host over gRPC.
    /// </summary>
    [TestClass]
    public class ExecRuntimeTests
    {
        const string Project = "SW.Serverless.Samples.Greedy";
        const string AdapterId = "test.exec";

        static IHost host;
        static IResidentAdapterHost adapters;
        static string workDirectory;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            workDirectory = Path.Combine(Path.GetTempPath(), "swsl-exectests", Guid.NewGuid().ToString("N"));
            var publishDirectory = Path.Combine(workDirectory, "publish");
            var platform = AdapterRuntimes.CurrentPlatform;

            var project = Path.Combine(RepositoryRoot(), Project, Project + ".csproj");
            using (var publish = Process.Start(new ProcessStartInfo("dotnet",
                       $"publish \"{project}\" -c Release -r {platform} --self-contained true -p:PublishSingleFile=true -o \"{publishDirectory}\" " +
                       // Built under the test's own folder, never beside the sample's usual build output,
                       // which other tests read.
                       $"--artifacts-path \"{Path.Combine(workDirectory, "artifacts")}\" -v q")
                   {
                       RedirectStandardOutput = true,
                       RedirectStandardError = true,
                       // No MSBuild nodes left holding the output open: see DotnetBuilds.
                       Environment = { ["MSBUILDDISABLENODEREUSE"] = "1", ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
                   }))
            {
                var output = await publish!.StandardOutput.ReadToEndAsync() + await publish.StandardError.ReadToEndAsync();
                await publish.WaitForExitAsync();
                Assert.AreEqual(0, publish.ExitCode, output);
            }

            var binary = Path.Combine(publishDirectory, OperatingSystem.IsWindows() ? Project + ".exe" : Project);
            Assert.IsTrue(File.Exists(binary), $"no single-file binary at {binary}");

            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-exec");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(workDirectory, "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-x{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-x{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(60);
                    });
                })
                .Build();
            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();

            // Packaged with only the binary and its manifest — no .dll anywhere for dotnet to start —
            // and stored as a version, as a non-.NET adapter is published.
            var zip = Path.Combine(workDirectory, "package.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(binary, Path.GetFileName(binary));
                using var writer = new StreamWriter(archive.CreateEntry("adapter.json").Open());
                writer.Write(new JObject
                {
                    ["id"] = AdapterId,
                    ["runtime"] = "exec",
                    ["lifecycle"] = "resident",
                    ["entry"] = Path.GetFileName(binary),
                    ["platforms"] = new JArray(platform),
                }.ToString());
            }

            var files = host.Services.GetRequiredService<ICloudFilesService>();
            await using (var stream = File.OpenRead(zip))
                await files.WriteAsync(stream, new WriteFileSettings
                {
                    Key = $"adapters-versions/{AdapterId}/1.0.0",
                    ContentType = "application/zip",
                    Metadata = new Dictionary<string, string>
                    {
                        ["EntryAssembly"] = Path.GetFileName(binary),
                        ["Hash"] = "exec-" + Guid.NewGuid().ToString("N")[..12],
                        ["Protocol"] = "2",
                        ["Lifecycle"] = "resident",
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

        static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
            return dir!.FullName;
        }

        [TestMethod]
        public async Task A_self_contained_binary_runs_as_itself_and_answers_over_the_protocol()
        {
            var instance = await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = $"{AdapterId}/1.0.0",
                InstanceKey = "exec",
            });

            Assert.AreEqual(InstanceState.Ready, instance.State);
            // Started as itself: the process is the binary, not dotnet hosting a dll.
            Assert.AreEqual(OperatingSystem.IsWindows() ? Project + ".exe" : Project, Path.GetFileName(instance.Process!.MainModule!.FileName));

            await instance.InvokeAsync<JObject>("Allocate", 3, timeoutSeconds: 30);
            var stats = await instance.InvokeAsync<JObject>("GetStats", timeoutSeconds: 15);
            Assert.AreEqual(3, stats!.Value<int>("allocatedMb"));

            Assert.AreEqual("dotnet", adapters.Describe().Single(h => h.InstanceKey == "exec").SdkLanguage,
                "a .NET adapter compiled to a binary still says which SDK it was built with");
            await adapters.StopAsync($"{AdapterId}/1.0.0", "exec", drain: false);
        }

        /// <summary>A classic session with an adapter in another runtime goes over gRPC, unchanged for the caller.</summary>
        [TestMethod]
        public async Task A_classic_session_with_an_exec_adapter_runs_over_grpc()
        {
            var service = host.Services.GetRequiredService<IServerlessService>();
            await service.StartAsync($"{AdapterId}/1.0.0", "corr-exec");
            try
            {
                await service.InvokeAsync("Allocate", 2);
                var stats = await service.InvokeAsync<JObject>("GetStats", null);
                Assert.AreEqual(2, stats.Value<int>("allocatedMb"));
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }
    }
}
