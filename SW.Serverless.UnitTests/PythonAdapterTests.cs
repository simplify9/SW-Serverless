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
using System.Text;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// Adapters written in Python with sw-serverless, run by the real host: classic sessions, one
    /// call at a time, resident instances, and adapters implementing the tests' sample "orders"
    /// contract. The SDK has no dependencies, so a package is the adapter's files and the SDK's,
    /// vendored beside them — what sw-serverless build makes.
    /// </summary>
    [TestClass]
    public class PythonAdapterTests
    {
        static IHost host;
        static string workDirectory;

        static readonly (string Id, string Script, string Lifecycle)[] Adapters =
        {
            ("test.python.classic", "classic.py", "classic"),
            ("test.python.resident", "resident.py", "resident"),
            ("test.python.processor", "orders_processor.py", "classic"),
            ("test.python.source", "orders_source.py", "classic"),
        };

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            workDirectory = Path.Combine(Path.GetTempPath(), "swsl-python", Guid.NewGuid().ToString("N"));
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-python");
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
                        o.SocketPath = $"/tmp/swsl-py{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-py{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(60);
                    });
                })
                .Build();
            await host.StartAsync();

            var files = host.Services.GetRequiredService<ICloudFilesService>();
            foreach (var (id, script, lifecycle) in Adapters)
            {
                using var package = PythonPackage.Build(id, script, lifecycle);
                await files.WriteAsync(package, new WriteFileSettings
                {
                    Key = $"adapters-versions/{id}/1.0.0",
                    ContentType = "application/zip",
                    Metadata = new Dictionary<string, string>
                    {
                        ["EntryAssembly"] = PythonPackage.Entry,
                        ["Hash"] = "py-" + Guid.NewGuid().ToString("N")[..12],
                        ["Protocol"] = "2",
                        ["Lifecycle"] = lifecycle,
                    }
                });
            }
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) await host.StopAsync();
            host?.Dispose();
            try { Directory.Delete(workDirectory, true); } catch { }
        }

        static IServerlessService Service() => host.Services.GetRequiredService<IServerlessService>();
        static IResidentAdapterHost Residents() => host.Services.GetRequiredService<IResidentAdapterHost>();

        static async Task<T> InSession<T>(string adapterId, IDictionary<string, string> values, Func<IServerlessService, Task<T>> call)
        {
            var service = Service();
            await service.StartAsync($"{adapterId}/1.0.0", "corr-py", values ?? new Dictionary<string, string>());
            try { return await call(service); }
            finally { ((IDisposable)service).Dispose(); }
        }

        [TestMethod]
        public async Task A_classic_python_adapter_answers_with_the_values_it_was_started_with()
        {
            await InSession("test.python.classic", new Dictionary<string, string> { ["Prefix"] = "hi " }, async s =>
            {
                Assert.AreEqual("hi world", await s.InvokeAsync<string>("Greet", "world"));
                Assert.AreEqual("corr-py", await s.InvokeAsync<string>("Correlation", null));
                Assert.AreEqual(5, await s.InvokeAsync<int>("Add", new { A = 2, B = 3 }));
                // Unicode survives both ways.
                Assert.AreEqual("hi مرحبا ✓", await s.InvokeAsync<string>("Greet", "مرحبا ✓"));
                return 0;
            });
        }

        [TestMethod]
        public async Task Payloads_past_the_http2_window_cross_both_ways()
        {
            // 64 KB is HTTP/2's default window: these only arrive if flow control works.
            await InSession("test.python.classic", null, async s =>
            {
                var big = await s.InvokeAsync<string>("Big", 3 * 1024 * 1024);
                Assert.AreEqual(3 * 1024 * 1024, big.Length);
                Assert.AreEqual(2 * 1024 * 1024 + 7, await s.InvokeAsync<int>("Length", new string('y', 2 * 1024 * 1024 + 7)));
                return 0;
            });
        }

        [TestMethod]
        public async Task An_error_reaches_the_caller_with_its_type_and_message()
        {
            await InSession("test.python.classic", null, async s =>
            {
                // Raised as the adapter raised it: its type, as the adapter named it, and its message.
                var rejected = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(() => s.InvokeAsync<string>("Fail", "no stock"));
                Assert.AreEqual("Acme.Rejected", rejected.AdapterExceptionType);
                StringAssert.Contains(rejected.Message, "no stock");

                var crashed = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(() => s.InvokeAsync<string>("Crash", null));
                Assert.AreEqual("KeyError", crashed.AdapterExceptionType);
                StringAssert.Contains(crashed.Detail, "Traceback");

                var unknown = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(() => s.InvokeAsync<string>("Teleport", null));
                StringAssert.Contains(unknown.Message, "Teleport");

                // The session is still good after all three.
                Assert.AreEqual("x", await s.InvokeAsync<string>("Big", 1));
                return 0;
            });
        }

        [TestMethod]
        public async Task A_call_that_times_out_is_cancelled_and_the_adapter_keeps_answering()
        {
            var residents = Residents();
            var instance = await residents.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = "test.python.classic/1.0.0",
                InstanceKey = "py-cancel",
                StartupValues = new Dictionary<string, string> { ["Prefix"] = "> " },
            });
            try
            {
                var started = DateTime.UtcNow;
                await Assert.ThrowsExceptionAsync<TimeoutException>(() => instance.InvokeAsync<string>("Slow", 30.0, timeoutSeconds: 1));
                Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));

                // Several at once on one instance, while the cancelled one is gone.
                var answers = await Task.WhenAll(Enumerable.Range(0, 20)
                    .Select(i => instance.InvokeAsync<string>("Greet", "n" + i, timeoutSeconds: 15)));
                CollectionAssert.AreEqual(Enumerable.Range(0, 20).Select(i => "> n" + i).ToArray(), answers);
            }
            finally
            {
                await residents.StopAsync("test.python.classic/1.0.0", "py-cancel", drain: false);
            }
        }

        [TestMethod]
        public async Task A_command_with_no_result_completes()
        {
            await InSession("test.python.classic", null, async s =>
            {
                await s.InvokeAsync("Nothing", null);
                return 0;
            });
        }

        [TestMethod]
        public async Task A_resident_python_adapter_starts_reports_status_publishes_keeps_state_resets_and_stops()
        {
            var residents = Residents();
            var sink = host.Services.GetRequiredService<TestEventSink>();
            var instance = await residents.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = "test.python.resident/1.0.0",
                InstanceKey = "py-resident",
            });
            try
            {
                Assert.AreEqual(InstanceState.Ready, instance.State);
                Assert.IsTrue(await instance.InvokeAsync<bool>("Started", timeoutSeconds: 15));

                var described = residents.Describe().Single(d => d.InstanceKey == "py-resident");
                Assert.AreEqual("python", described.SdkLanguage);

                var pong = await instance.PingAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("Listening", pong.State);
                Assert.IsTrue(pong.Connected);

                var reference = await instance.InvokeAsync<string>("Publish", "order-1", timeoutSeconds: 15);
                StringAssert.StartsWith(reference, "ref-");
                Assert.IsTrue(sink.Delivered.Any(d => d.Body == "order-1" && d.DedupeKey == "k-order-1" && d.Endpoint == "tests"));

                // An empty string is an empty payload, which reads back as null — as from a .NET adapter.
                Assert.IsTrue(string.IsNullOrEmpty(await instance.InvokeAsync<string>("Remember", "first", timeoutSeconds: 15)));
                Assert.AreEqual("first", await instance.InvokeAsync<string>("Remember", "second", timeoutSeconds: 15));
                await instance.InvokeAsync<object>("Forget", timeoutSeconds: 15);
                Assert.IsTrue(string.IsNullOrEmpty(await instance.InvokeAsync<string>("Remember", "third", timeoutSeconds: 15)));

                await instance.ResetAsync("session-9");
                CollectionAssert.AreEqual(new[] { "session-9" }, await instance.InvokeAsync<string[]>("Resets", timeoutSeconds: 15));
            }
            finally
            {
                await residents.StopAsync("test.python.resident/1.0.0", "py-resident", drain: true);
            }
            Assert.IsFalse(residents.Describe().Any(d => d.InstanceKey == "py-resident"));
        }

        [TestMethod]
        public async Task An_orders_processor_in_python_takes_and_returns_json_objects()
        {
            var receipt = await InSession("test.python.processor", new Dictionary<string, string> { ["Partner"] = "acme" }, s =>
                s.InvokeAsync<JObject>("Process", new { OrderId = "SO-1", Lines = 2 }));
            Assert.IsTrue((bool)receipt["Accepted"]);
            Assert.AreEqual("acme:SO-1", (string)receipt["Reference"]);

            var refused = await InSession("test.python.processor", new Dictionary<string, string> { ["Partner"] = "acme" }, s =>
                s.InvokeAsync<JObject>("Process", new { OrderId = "SO-2" }));
            Assert.IsFalse((bool)refused["Accepted"]);
        }

        [TestMethod]
        public async Task An_orders_source_in_python_runs_a_session_in_the_contract_s_order()
        {
            var root = Path.Combine(workDirectory, "source");
            var folder = Path.Combine(root, "inbox");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a"), "1");
            File.WriteAllText(Path.Combine(folder, "b"), "22");

            var taken = await InSession("test.python.source", new Dictionary<string, string> { ["Folder"] = folder }, async s =>
            {
                var got = new List<string>();
                await s.InvokeAsync("Open", null);
                foreach (var id in await s.InvokeAsync<string[]>("List", null))
                {
                    var order = await s.InvokeAsync<JObject>("Fetch", id);
                    got.Add((string)order["OrderId"] + "=" + (int)order["Lines"]);
                    await s.InvokeAsync("Remove", id);
                }
                await s.InvokeAsync("Close", null);
                return got;
            });

            CollectionAssert.AreEqual(new[] { "a=1", "b=2" }, taken);
            Assert.AreEqual(0, Directory.GetFiles(folder).Length);
            Assert.AreEqual("Open,List,Fetch,Remove,Fetch,Remove,Close", File.ReadAllText(Path.Combine(root, "calls.txt")));
        }

        [TestMethod]
        public async Task A_python_adapter_describes_itself_for_the_manifest()
        {
            var dir = Path.Combine(workDirectory, "describe");
            using (var package = PythonPackage.Build("test.python.processor", "orders_processor.py", "classic"))
            using (var archive = new ZipArchive(package))
                archive.ExtractToDirectory(dir);

            var (description, problem) = await Tooling.LocalAdapterHost.DescribeAsync(
                Path.Combine(dir, PythonPackage.Entry), "python");

            Assert.IsNotNull(description, problem);
            Assert.AreEqual("python", description.SdkLanguage);
            Assert.AreEqual("classic", description.Lifecycle);
            Assert.AreEqual(2, description.Protocol.Min);
            CollectionAssert.AreEqual(new[] { "processor" }, description.Kinds);
            Assert.AreEqual(1, description.Contracts["orders"]);
            var partner = description.Settings.Single();
            Assert.AreEqual("Partner", partner.Name);
            Assert.IsTrue(partner.Required);
            var process = description.Commands.Single();
            Assert.AreEqual("Process", process.Name);
            Assert.AreEqual("object", process.InputSchema!.Value.GetProperty("type").GetString());
            Assert.IsTrue(process.ReturnsValue);
        }
    }

    /// <summary>
    /// A Python adapter packaged as sw-serverless build packages one: the script as main.py, the SDK
    /// under _vendor/, and an entry that puts _vendor on the path before running main.py.
    /// </summary>
    static class PythonPackage
    {
        public const string Entry = "_serverless_entry.py";

        const string Bootstrap = """
            import os, runpy, sys
            here = os.path.dirname(os.path.abspath(__file__))
            sys.path.insert(0, os.path.join(here, "_vendor"))
            sys.path.insert(0, here)
            runpy.run_path(os.path.join(here, "main.py"), run_name="__main__")
            """;

        public static MemoryStream Build(string id, string script, string lifecycle)
        {
            var root = RepositoryRoot();
            var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                void AddText(string name, string text)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }

                void AddFolder(string folder, string under)
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.py", SearchOption.AllDirectories))
                        archive.CreateEntryFromFile(file, under + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
                }

                archive.CreateEntryFromFile(Path.Combine(root, "SW.Serverless.UnitTests", "PythonAdapters", script), "main.py");
                AddText(Entry, Bootstrap);
                AddFolder(Path.Combine(root, "sdk", "python", "src", "sw_serverless"), "_vendor/sw_serverless");
                AddText("adapter.json", new JObject
                {
                    ["id"] = id,
                    ["version"] = "1.0.0",
                    ["runtime"] = "python",
                    ["lifecycle"] = lifecycle,
                    ["protocol"] = new JObject { ["min"] = 2, ["max"] = 2 },
                    ["entry"] = Entry,
                }.ToString());
            }
            buffer.Position = 0;
            return buffer;
        }

        static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }
}
