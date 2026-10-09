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
    /// Adapters written in JavaScript and TypeScript with @simplyworks/serverless, built by serverless
    /// build and run by the real host: classic sessions as Bitween runs handlers, resident instances,
    /// and the Bitween kinds written with @simplyworks/bitween — the validator in TypeScript.
    /// </summary>
    [TestClass]
    public class NodeAdapterTests
    {
        static IHost host;
        static string workDirectory;

        static readonly (string Id, string Script, string Lifecycle)[] Adapters =
        {
            ("test.node.classic", "classic.js", "classic"),
            ("test.node.resident", "resident.js", "resident"),
            ("test.node.handler", "bitween_handler.js", "classic"),
            ("test.node.receiver", "bitween_receiver.js", "classic"),
            ("test.node.validator", "bitween_validator.ts", "classic"),
        };

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            workDirectory = Path.Combine(Path.GetTempPath(), "swsl-node", Guid.NewGuid().ToString("N"));
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-node");
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
                        o.SocketPath = $"/tmp/swsl-nd{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-nd{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(60);
                    });
                })
                .Build();
            await host.StartAsync();

            var files = host.Services.GetRequiredService<ICloudFilesService>();
            foreach (var (id, script, lifecycle) in Adapters)
            {
                if (script.EndsWith(".ts") && !NodePackage.CanStripTypes) continue;
                var (zip, entry) = await NodePackage.BuildAsync(workDirectory, id, script);
                await using var package = File.OpenRead(zip);
                await files.WriteAsync(package, new WriteFileSettings
                {
                    Key = $"adapters-versions/{id}/1.0.0",
                    ContentType = "application/zip",
                    Metadata = new Dictionary<string, string>
                    {
                        ["EntryAssembly"] = entry,
                        ["Hash"] = "nd-" + Guid.NewGuid().ToString("N")[..12],
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
            await service.StartAsync($"{adapterId}/1.0.0", "corr-nd", values ?? new Dictionary<string, string>());
            try { return await call(service); }
            finally { ((IDisposable)service).Dispose(); }
        }

        [TestMethod]
        public async Task A_classic_node_adapter_answers_with_the_values_it_was_started_with()
        {
            await InSession("test.node.classic", new Dictionary<string, string> { ["Prefix"] = "hi " }, async s =>
            {
                Assert.AreEqual("hi world", await s.InvokeAsync<string>("Greet", "world"));
                Assert.AreEqual("corr-nd", await s.InvokeAsync<string>("Correlation", null));
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
            await InSession("test.node.classic", null, async s =>
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
            await InSession("test.node.classic", null, async s =>
            {
                // Raised as the adapter raised it: its type, as the adapter named it, and its message.
                var rejected = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(() => s.InvokeAsync<string>("Fail", "no stock"));
                Assert.AreEqual("Acme.Rejected", rejected.AdapterExceptionType);
                StringAssert.Contains(rejected.Message, "no stock");

                var crashed = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(() => s.InvokeAsync<string>("Crash", null));
                Assert.AreEqual("TypeError", crashed.AdapterExceptionType);
                StringAssert.Contains(crashed.Detail, "main.js");

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
                AdapterId = "test.node.classic/1.0.0",
                InstanceKey = "nd-cancel",
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
                await residents.StopAsync("test.node.classic/1.0.0", "nd-cancel", drain: false);
            }
        }

        [TestMethod]
        public async Task A_command_with_no_result_completes()
        {
            await InSession("test.node.classic", null, async s =>
            {
                await s.InvokeAsync("Nothing", null);
                return 0;
            });
        }

        [TestMethod]
        public async Task A_resident_node_adapter_starts_reports_status_publishes_keeps_state_resets_and_stops()
        {
            var residents = Residents();
            var sink = host.Services.GetRequiredService<TestEventSink>();
            var instance = await residents.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = "test.node.resident/1.0.0",
                InstanceKey = "nd-resident",
            });
            try
            {
                Assert.AreEqual(InstanceState.Ready, instance.State);
                Assert.IsTrue(await instance.InvokeAsync<bool>("Started", timeoutSeconds: 15));

                var described = residents.Describe().Single(d => d.InstanceKey == "nd-resident");
                Assert.AreEqual("node", described.SdkLanguage);

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
                await residents.StopAsync("test.node.resident/1.0.0", "nd-resident", drain: true);
            }
            Assert.IsFalse(residents.Describe().Any(d => d.InstanceKey == "nd-resident"));
        }

        [TestMethod]
        public async Task A_bitween_handler_in_javascript_takes_and_returns_exchange_files_as_dotnet_ones()
        {
            var answer = await InSession("test.node.handler", new Dictionary<string, string> { ["Partner"] = "acme" }, s =>
                s.InvokeAsync<JObject>("Handle", new { Data = "{\"orderId\":\"SO-1\"}", Filename = "order.json", BadData = false }));

            Assert.AreEqual("answer.json", (string)answer["Filename"]);
            Assert.IsFalse((bool)answer["BadData"]);
            var data = JObject.Parse((string)answer["Data"]);
            Assert.AreEqual("acme", (string)data["to"]);
            Assert.AreEqual("SO-1", (string)data["orderId"]);
            Assert.AreEqual("order.json", (string)data["from"]);
            // Hash is what .NET's ExchangeFile computes: SHA-1 of Data, lower-case hex.
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            Assert.AreEqual(Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes((string)answer["Data"]))).ToLowerInvariant(),
                (string)answer["Hash"]);

            var rejected = await InSession("test.node.handler", new Dictionary<string, string> { ["Partner"] = "acme" }, s =>
                s.InvokeAsync<JObject>("Handle", new { Data = "{\"reject\":true}" }));
            Assert.IsTrue((bool)rejected["BadData"], "a rejected delivery is returned, not raised");
        }

        [TestMethod]
        public async Task A_bitween_validator_in_typescript_reports_each_failure()
        {
            NodePackage.RequireTypeStripping();
            var result = await InSession("test.node.validator", null, s =>
                s.InvokeAsync<JObject>("Validate", new { Data = "{\"lines\":[]}" }));

            Assert.IsFalse((bool)result["Success"]);
            CollectionAssert.AreEqual(new[] { "orderId", "lines" },
                result["Validations"]!.Select(v => (string)v["Key"]).ToArray());

            var valid = await InSession("test.node.validator", null, s =>
                s.InvokeAsync<JObject>("Validate", new { Data = "{\"orderId\":\"SO-1\",\"lines\":[1]}" }));
            Assert.IsTrue((bool)valid["Success"]);
        }

        [TestMethod]
        public async Task A_bitween_receiver_in_javascript_runs_a_session_in_the_contract_s_order()
        {
            var root = Path.Combine(workDirectory, "receiver");
            var folder = Path.Combine(root, "inbox");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.json"), "{\"n\":1}");
            File.WriteAllText(Path.Combine(folder, "b.json"), "{\"n\":2}");

            var taken = await InSession("test.node.receiver", new Dictionary<string, string> { ["Folder"] = folder }, async s =>
            {
                var got = new List<string>();
                await s.InvokeAsync("Initialize", null);
                foreach (var id in await s.InvokeAsync<string[]>("ListFiles", null))
                {
                    var file = await s.InvokeAsync<JObject>("GetFile", id);
                    got.Add((string)file["Filename"] + "=" + (string)file["Data"]);
                    await s.InvokeAsync("DeleteFile", id);
                }
                await s.InvokeAsync("Finalize", null);
                return got;
            });

            CollectionAssert.AreEqual(new[] { "a.json={\"n\":1}", "b.json={\"n\":2}" }, taken);
            Assert.AreEqual(0, Directory.GetFiles(folder).Length);
            Assert.AreEqual("Initialize,ListFiles,GetFile,DeleteFile,GetFile,DeleteFile,Finalize",
                File.ReadAllText(Path.Combine(root, "calls.txt")));
        }

        [TestMethod]
        public async Task A_node_adapter_describes_itself_for_the_manifest_and_typescript_runs_as_javascript()
        {
            NodePackage.RequireTypeStripping();
            var (zip, entry) = await NodePackage.BuildAsync(workDirectory, "test.node.describe", "bitween_validator.ts");
            Assert.AreEqual("main.js", entry, "the TypeScript entry runs as the JavaScript Node strips it to");

            using var archive = ZipFile.OpenRead(zip);
            var manifest = Contract.Catalog.AdapterManifest.Parse(new StreamReader(archive.GetEntry("adapter.json")!.Open()).ReadToEnd());
            Assert.AreEqual("node", manifest.Runtime);
            Assert.AreEqual("typescript", manifest.Language);
            Assert.AreEqual(2, manifest.Protocol.Min);
            CollectionAssert.AreEqual(new[] { "validator" }, manifest.Kinds);
            Assert.AreEqual(1, manifest.Contracts["bitween"]);
            Assert.IsNotNull(archive.GetEntry("node_modules/@simplyworks/serverless/src/index.js"));
            Assert.IsNotNull(archive.GetEntry("node_modules/@simplyworks/bitween/src/index.js"));
            Assert.IsNull(archive.GetEntry("main.ts"), "the package runs JavaScript");
            Assert.IsNotNull(archive.GetEntry("source/main.ts"), "the source is what was written");
        }
    }

    /// <summary>A Node adapter's project — the script as main.js or main.ts, and its adapter.json — built by serverless build.</summary>
    static class NodePackage
    {
        static readonly Lazy<bool> canStripTypes = new(() =>
        {
            try
            {
                using var node = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node",
                    "-e \"process.exit(typeof require('node:module').stripTypeScriptTypes === 'function' ? 0 : 1)\"")
                    { RedirectStandardOutput = true, RedirectStandardError = true });
                node!.WaitForExit();
                return node.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });

        /// <summary>Whether this machine's Node strips TypeScript types, which takes 22.13 or later.</summary>
        public static bool CanStripTypes => canStripTypes.Value;

        public static void RequireTypeStripping()
        {
            if (!CanStripTypes) Assert.Inconclusive("this machine's node can't strip TypeScript types; it takes Node 22.13 or later");
        }

        public static async Task<(string Zip, string Entry)> BuildAsync(string work, string id, string script)
        {
            var root = RepositoryRoot();
            var project = Path.Combine(work, "projects", id);
            Directory.CreateDirectory(project);
            var entry = "main" + Path.GetExtension(script);
            File.Copy(Path.Combine(root, "SW.Serverless.UnitTests", "NodeAdapters", script), Path.Combine(project, entry));
            File.WriteAllText(Path.Combine(project, "adapter.json"), new JObject
            {
                ["id"] = id,
                ["version"] = "1.0.0",
                ["runtime"] = "node",
                ["entry"] = entry,
            }.ToString());

            var built = await Tooling.Building.PackageBuilder.BuildAsync(new Tooling.Building.BuildRequest
            {
                ProjectDirectory = project,
                OutputDirectory = Path.Combine(work, "built", id),
            });
            Assert.IsTrue(built.Succeeded, string.Join("; ", built.Problems));
            return (built.ZipPath, built.Manifest.Entry);
        }

        static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }
}
