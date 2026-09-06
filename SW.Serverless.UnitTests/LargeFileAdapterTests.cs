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
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// The large-file streaming adapter, which is also the end-to-end proof that the adapter-side
    /// DI container works: this handler takes IOptions, an injected reader and an ILogger through
    /// its constructor, and none of that can resolve unless the container was built from real
    /// startup values.
    /// </summary>
    [TestClass]
    public class LargeFileAdapterTests
    {
        const string AdapterId = "test.largefiles";
        const int ChunkKb = 64;

        static IHost host;
        static IResidentAdapterHost adapters;
        static TestEventSink sink;
        static string root;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            root = Path.Combine(Path.GetTempPath(), "swsl-filetests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-files");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(root, "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-f{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-f{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(45);
                        o.MaxInFlight = 4;
                    });
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                })
                .Build();

            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();
            sink = host.Services.GetRequiredService<TestEventSink>();

            await TestStore.PublishAsync(host.Services.GetRequiredService<ICloudFilesService>(),
                AdapterId, "SW.Serverless.Samples.LargeFiles",
                new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) { await host.StopAsync(); host.Dispose(); }
            try { Directory.Delete(root, true); } catch { }
        }

        [TestInitialize]
        public void TestInitialize()
        {
            sink.Clear();
            sink.RejectEverything = false;
        }

        [TestCleanup]
        public async Task TestCleanup()
        {
            foreach (var health in adapters.Describe())
                await adapters.StopAsync(health.AdapterId, health.InstanceKey, drain: false);
        }

        /// <summary>
        /// The handler cannot even be constructed unless IOptions&lt;StreamOptions&gt;,
        /// IChunkReader and ILogger&lt;T&gt; all resolve — so an adapter that starts at all has
        /// proved the DI container bound its startup values.
        /// </summary>
        [TestMethod]
        public async Task An_adapter_using_constructor_injection_starts()
        {
            var instance = await Start("di");

            Assert.AreEqual(InstanceState.Ready, instance.State);

            var progress = await instance.InvokeAsync<JObject>("GetProgress");
            Assert.AreEqual("Idle", progress.Value<string>("state"));
        }

        [TestMethod]
        public async Task A_file_is_streamed_as_ordered_chunks_that_reassemble()
        {
            var instance = await Start("stream");

            await instance.InvokeAsync<JObject>("GenerateTestFile", 2, timeoutSeconds: 60);

            var expected = 2 * 1024 / ChunkKb;
            await WaitFor(() => sink.Delivered.Count(d => d.Accepted) >= expected,
                TimeSpan.FromSeconds(60), $"expected {expected} chunks");

            var progress = await instance.InvokeAsync<JObject>("GetProgress");
            Assert.AreEqual(1, progress.Value<int>("filesCompleted"));
            Assert.AreEqual(0, progress.Value<int>("chunksRejected"));

            // Headers must carry enough for the host to reassemble in order.
            var keys = sink.Delivered.Where(d => d.Accepted).Select(d => d.DedupeKey).ToList();
            Assert.IsTrue(keys.All(k => k.StartsWith("filestream:")));
            Assert.AreEqual(keys.Count, keys.Distinct().Count(), "every chunk needs a distinct key");

            // Chunk index is the last segment; it must run 0..n-1 with no gaps.
            var indexes = keys.Select(k => int.Parse(k[(k.LastIndexOf(':') + 1)..])).OrderBy(i => i).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, indexes.Length).ToArray(), indexes);
        }

        /// <summary>
        /// The point of the sample: memory tracks the CHUNK size, not the file size. A 24 MB file
        /// through a 64 KB chunker must not move the adapter's working set meaningfully.
        /// </summary>
        [TestMethod]
        public async Task Memory_stays_flat_while_a_large_file_streams()
        {
            var instance = await Start("memory");

            var before = (await instance.InvokeAsync<JObject>("GetProgress"))
                .Value<long>("selfWorkingSetMb");

            await instance.InvokeAsync<JObject>("GenerateTestFile", 24, timeoutSeconds: 120);

            var peak = before;
            await WaitFor(async () =>
            {
                var progress = await instance.InvokeAsync<JObject>("GetProgress");
                peak = Math.Max(peak, progress.Value<long>("selfWorkingSetMb"));
                return progress.Value<int>("filesCompleted") == 1;
            }, TimeSpan.FromSeconds(120), "the file never finished streaming");

            Assert.IsTrue(peak - before < 24,
                $"working set grew {peak - before} MB while streaming a 24 MB file — it is buffering, not streaming");
        }

        /// <summary>
        /// A partial file is worse than no file, so a rejected chunk abandons the whole transfer
        /// and leaves the source in place to be retried from the beginning.
        /// </summary>
        [TestMethod]
        public async Task A_rejected_chunk_abandons_the_file_and_leaves_it_in_place()
        {
            sink.RejectEverything = true;
            var instance = await Start("reject");

            var generated = await instance.InvokeAsync<JObject>("GenerateTestFile", 2, timeoutSeconds: 60);
            var name = generated.Value<string>("file");

            await WaitFor(async () =>
                (await instance.InvokeAsync<JObject>("GetProgress")).Value<int>("chunksRejected") > 0,
                TimeSpan.FromSeconds(60), "no chunk was rejected");

            var progress = await instance.InvokeAsync<JObject>("GetProgress");
            Assert.AreEqual(0, progress.Value<int>("filesCompleted"),
                "a partially rejected file must not count as completed");

            Assert.IsTrue(File.Exists(Path.Combine(RootFor("reject"), name)),
                "the source file must stay put so the whole transfer can be retried");
        }

        [TestMethod]
        public async Task Streaming_can_be_paused_and_resumed()
        {
            // A heavier throttle so the transfer is still in flight when Pause lands. At 2 ms a
            // chunk the whole file can finish before the poll below returns, and then the test is
            // pausing nothing and waiting for acknowledgements that can never arrive.
            var instance = await Start("pause", throttleMsPerChunk: 60);

            await instance.InvokeAsync<JObject>("GenerateTestFile", 8, timeoutSeconds: 60);
            await WaitFor(async () =>
                {
                    var progress = await instance.InvokeAsync<JObject>("GetProgress");
                    return progress.Value<int>("chunksAcked") > 2
                           && progress.Value<string>("state") == "Streaming";
                },
                TimeSpan.FromSeconds(60), "streaming never started");

            var paused = await instance.InvokeAsync<JObject>("Pause");
            Assert.IsTrue(paused.Value<bool>("paused"));

            var atPause = (await instance.InvokeAsync<JObject>("GetProgress")).Value<int>("chunksAcked");
            await Task.Delay(1500);
            var later = (await instance.InvokeAsync<JObject>("GetProgress")).Value<int>("chunksAcked");

            Assert.IsTrue(later - atPause <= 1,
                $"streaming continued while paused ({atPause} -> {later})");

            await instance.InvokeAsync<JObject>("Resume");
            await WaitFor(async () =>
                (await instance.InvokeAsync<JObject>("GetProgress")).Value<int>("chunksAcked") > later,
                TimeSpan.FromSeconds(30), "streaming did not resume");
        }

        [TestMethod]
        public async Task Progress_reaches_the_health_view_through_the_heartbeat()
        {
            // Throttled so the transfer is still running when the heartbeat samples it. At full
            // speed the file can complete between two heartbeats and progress never appears —
            // the test would then be asserting on timing, not on the heartbeat.
            var instance = await Start("progress", throttleMsPerChunk: 40);
            await instance.InvokeAsync<JObject>("GenerateTestFile", 16, timeoutSeconds: 90);

            await WaitFor(() => adapters.Describe()
                    .Single(h => h.InstanceKey == "progress")
                    .Details.ContainsKey("percent"),
                TimeSpan.FromSeconds(60), "progress never appeared on the heartbeat");

            var details = adapters.Describe().Single(h => h.InstanceKey == "progress").Details;

            Assert.IsTrue(details.ContainsKey("currentFile"));
            Assert.IsTrue(details.ContainsKey("throughputMbPerSec"));
            Assert.IsTrue(int.Parse(details["percent"]) is >= 0 and <= 100);
        }

        // ------------------------------------------------------------------ helpers

        static string RootFor(string key) => Path.Combine(root, key);

        static Task<ResidentAdapterInstance> Start(string key, int throttleMsPerChunk = 2)
        {
            Directory.CreateDirectory(RootFor(key));
            return adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = AdapterId,
                InstanceKey = key,
                StartupValues =
                {
                    ["Path"] = RootFor(key),
                    ["Pattern"] = "*.bin",
                    ["ChunkSizeKb"] = ChunkKb.ToString(),
                    ["PollSeconds"] = "1",
                    ["ThrottleMsPerChunk"] = throttleMsPerChunk.ToString()
                }
            });
        }

        static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string because)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(200);
            }
            Assert.Fail($"Timed out after {timeout}: {because}");
        }

        static async Task WaitFor(Func<Task<bool>> condition, TimeSpan timeout, string because)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try { if (await condition()) return; } catch { }
                await Task.Delay(300);
            }
            Assert.Fail($"Timed out after {timeout}: {because}");
        }
    }
}
