using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Resident;
using SW.Serverless.UnitTests.Fixtures;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// A classic session — start, call, dispose, as Bitween runs handlers — with an adapter that
    /// speaks gRPC: what every adapter in a language other than .NET does, and a .NET adapter
    /// whose manifest opts in with protocol 2. IServerlessService is unchanged for the caller; the
    /// session runs on a resident instance of its own.
    /// </summary>
    [TestClass]
    public class GrpcClassicTests
    {
        const string Project = "SW.Serverless.UnitTests.GrpcClassicAdapter";
        const string AdapterId = "test.grpcclassic";

        static IHost host;
        static string workDirectory;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            workDirectory = Path.Combine(Path.GetTempPath(), "swsl-grpcclassic", Guid.NewGuid().ToString("N"));
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-grpcclassic");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(workDirectory, "installed");
                        o.AdapterMetadataCacheDuration = 1;
                        o.CommandTimeout = 30;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-gc{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-gc{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(60);
                    });
                })
                .Build();
            await host.StartAsync();

            var output = TestStore.LocateBuildOutput(Project);
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(output, file), CompressionLevel.Fastest);
                using var writer = new StreamWriter(archive.CreateEntry("adapter.json").Open());
                writer.Write(new JObject
                {
                    ["id"] = AdapterId,
                    ["entry"] = Project + ".dll",
                    ["lifecycle"] = "classic",
                    ["protocol"] = new JObject { ["min"] = 2, ["max"] = 2 },
                }.ToString());
            }
            buffer.Position = 0;
            await host.Services.GetRequiredService<ICloudFilesService>().WriteAsync(buffer, new WriteFileSettings
            {
                Key = $"adapters/{AdapterId}",
                ContentType = "application/zip",
                Metadata = new Dictionary<string, string>
                {
                    ["EntryAssembly"] = Project + ".dll",
                    ["Hash"] = "gc-" + Guid.NewGuid().ToString("N")[..12],
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

        static IServerlessService Service() => host.Services.GetRequiredService<IServerlessService>();

        static IEnumerable<string> RunningKeys() =>
            host.Services.GetRequiredService<IResidentAdapterHost>().Describe().Select(h => h.InstanceKey);

        [TestMethod]
        public async Task A_session_without_a_correlation_id_or_with_null_values_still_starts()
        {
            // A protobuf map can't hold a null: Bitween's gateway runs validators with no correlation
            // id, and those sessions failed before the adapter was ready.
            var service = Service();
            await service.StartAsync(AdapterId, null, new Dictionary<string, string> { ["Prefix"] = "hi ", ["Unset"] = null });
            try
            {
                Assert.AreEqual("hi world", await service.InvokeAsync<string>("Greet", "world"));
                Assert.IsTrue(string.IsNullOrEmpty(await service.InvokeAsync<string>("Correlation", null)));
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }

        [TestMethod]
        public async Task A_call_returns_the_adapter_s_answer_using_the_startup_values_it_was_given()
        {
            var service = Service();
            await service.StartAsync(AdapterId, "corr-1", new Dictionary<string, string> { ["Prefix"] = "hi " });
            try
            {
                Assert.AreEqual("hi world", await service.InvokeAsync<string>("Greet", "world"));
                Assert.AreEqual("corr-1", await service.InvokeAsync<string>("Correlation", null));
                Assert.AreEqual(5, await service.InvokeAsync<int>("Add", new { A = 2, B = 3 }));
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }

        [TestMethod]
        public async Task A_declared_default_applies_when_no_value_is_given()
        {
            var service = Service();
            await service.StartAsync(AdapterId, "corr-2");
            try
            {
                Assert.AreEqual("hello world", await service.InvokeAsync<string>("Greet", "world"));
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }

        [TestMethod]
        public async Task The_expected_startup_values_are_the_settings_it_declared()
        {
            var service = Service();
            await service.StartAsync(AdapterId, "corr-3");
            try
            {
                var expected = await service.GetExpectedStartupValues();

                Assert.IsTrue(expected["Prefix"].Optional);
                Assert.AreEqual("hello ", expected["Prefix"].Default);
                Assert.IsFalse(expected["ApiKey"].Optional);
                Assert.IsTrue(expected["ApiKey"].Private);
                Assert.AreEqual("The partner's key.", expected["ApiKey"].Description);
                Assert.IsNull(expected["ApiKey"].Default);
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }

        [TestMethod]
        public async Task Its_handshake_names_its_language_kinds_and_contracts()
        {
            var before = RunningKeys().ToHashSet();
            var service = Service();
            await service.StartAsync(AdapterId, "corr-7");
            try
            {
                var described = host.Services.GetRequiredService<IResidentAdapterHost>().Describe()
                    .Single(h => !before.Contains(h.InstanceKey));
                Assert.AreEqual("dotnet", described.SdkLanguage);
                CollectionAssert.AreEqual(new[] { "handler" }, described.Kinds.ToList());
                Assert.AreEqual(1, described.Contracts["bitween"]);
                Assert.AreEqual("10.1.0", described.SdkVersion);
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }

        [TestMethod]
        public async Task An_adapter_error_fails_the_call_with_its_message()
        {
            var service = Service();
            await service.StartAsync(AdapterId, "corr-4");
            try
            {
                var error = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(() => service.InvokeAsync("Fail", null));
                StringAssert.Contains(error.Message, "asked to fail");
            }
            finally
            {
                ((IDisposable)service).Dispose();
            }
        }

        [TestMethod]
        public async Task Disposing_the_session_stops_its_process()
        {
            var before = RunningKeys().ToHashSet();
            var service = Service();
            await service.StartAsync(AdapterId, "corr-5");
            var mine = RunningKeys().Except(before).Single();
            StringAssert.StartsWith(mine, "classic-");

            ((IDisposable)service).Dispose();

            CollectionAssert.DoesNotContain(RunningKeys().ToList(), mine);
        }

        [TestMethod]
        public async Task Without_the_resident_host_registered_the_error_says_what_to_register()
        {
            using var bare = new ServiceCollection()
                .AddLogging(l => l.ClearProviders())
                // The same storage, so the package and its metadata are the ones the host published.
                .AddSingleton(host.Services.GetRequiredService<ICloudFilesService>())
                .AddServerless(o =>
                {
                    o.AdapterRemotePath = "adapters";
                    o.AdapterLocalPath = Path.Combine(workDirectory, "installed-bare");
                })
                .BuildServiceProvider();

            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                bare.GetRequiredService<IServerlessService>().StartAsync(AdapterId, "corr-6"));
            StringAssert.Contains(error.Message, "AddResidentAdapters");
        }
    }
}
