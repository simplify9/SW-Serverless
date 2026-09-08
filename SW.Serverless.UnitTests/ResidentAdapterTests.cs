using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Resident;
using SW.Serverless.UnitTests.Fixtures;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    [TestClass]
    public class ResidentAdapterTests
    {
        const string TickerId = "resident.ticker";
        const string FolderId = "resident.foldersource";

        static IHost host;
        static IResidentAdapterHost adapters;
        static TestEventSink sink;
        static string inbox;
        static string archive;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            inbox = Path.Combine(Path.GetTempPath(), "swsl-restests", Guid.NewGuid().ToString("N"));
            archive = Path.Combine(inbox, ".archive");
            Directory.CreateDirectory(inbox);

            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-resident");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-restests", "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-t{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-t{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
                    });
                })
                .Build();

            await host.StartAsync();

            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();
            sink = host.Services.GetRequiredService<TestEventSink>();

            var cloudFiles = host.Services.GetRequiredService<ICloudFilesService>();
            await TestStore.PublishAsync(cloudFiles, TickerId, "SW.Serverless.Samples.Ticker",
                new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });
            await TestStore.PublishAsync(cloudFiles, FolderId, "SW.Serverless.Samples.FolderSource",
                new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) await host.StopAsync();
            host?.Dispose();
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "swsl-restests"), true); } catch { }
        }

        // ------------------------------------------------------------------ installation

        [TestMethod]
        public async Task Installs_from_cloud_storage_and_attaches()
        {
            // No path anywhere: only an id. Download, extract, spawn and attach all follow from it.
            var instance = await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = TickerId,
                InstanceKey = "install",
                StartupValues = { ["IntervalSeconds"] = "1" }
            });

            Assert.AreEqual(InstanceState.Ready, instance.State);

            var health = adapters.Describe().Single(h => h.InstanceKey == "install");
            Assert.IsTrue(health.ProcessId > 0, "the adapter should be running in its own process");

            await adapters.StopAsync(TickerId, "install", drain: false);
        }

        // ------------------------------------------------------------------ commands

        [TestMethod]
        public async Task Invoke_returns_a_typed_result()
        {
            var instance = await StartTicker("invoke");

            var counters = await instance.InvokeAsync<Dictionary<string, object>>("GetCounters");

            Assert.IsNotNull(counters);
            Assert.IsTrue(counters.ContainsKey("produced"));

            await adapters.StopAsync(TickerId, "invoke", drain: false);
        }

        [TestMethod]
        public async Task Failing_command_surfaces_a_typed_error()
        {
            var instance = await StartTicker("explode");

            var ex = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(
                () => instance.InvokeAsync<object>("Explode"));

            Assert.AreEqual("System.InvalidOperationException", ex.AdapterExceptionType);
            StringAssert.Contains(ex.Message, "Deliberate failure");

            // The adapter survives a failed command; only that call failed.
            Assert.AreEqual(InstanceState.Ready, instance.State);

            await adapters.StopAsync(TickerId, "explode", drain: false);
        }

        /// <summary>
        /// The v1 regression. There, a timed-out command left the child running and its late
        /// reply resolved the NEXT caller's completion — in Traxis that reliably corrupted the
        /// GetLogs fetch issued right after. Here the timed-out call is removed, so the late
        /// reply is discarded and the following call still gets its own answer.
        /// </summary>
        [TestMethod]
        public async Task A_timed_out_command_does_not_corrupt_the_next_call()
        {
            var instance = await StartTicker("timeout");

            await Assert.ThrowsExceptionAsync<TimeoutException>(
                () => instance.InvokeAsync<object>("Sleep", 4, timeoutSeconds: 1));

            // Wait past the point where the stale reply arrives.
            await Task.Delay(TimeSpan.FromSeconds(5));

            var counters = await instance.InvokeAsync<Dictionary<string, object>>("GetCounters", timeoutSeconds: 10);

            Assert.IsNotNull(counters);
            Assert.IsTrue(counters.ContainsKey("produced"),
                "the next call must get its OWN result, not the orphaned Sleep reply");
            Assert.IsFalse(counters.ContainsKey("sleptSeconds"));

            await adapters.StopAsync(TickerId, "timeout", drain: false);
        }

        [TestMethod]
        public async Task Heartbeat_is_answered_while_a_command_is_running()
        {
            var instance = await StartTicker("multiplex");

            var slow = instance.InvokeAsync<object>("Sleep", 4, timeoutSeconds: 20);

            var stopwatch = Stopwatch.StartNew();
            var pong = await instance.PingAsync(TimeSpan.FromSeconds(3));
            stopwatch.Stop();

            Assert.IsNotNull(pong);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"the heartbeat queued behind the command ({stopwatch.Elapsed}); the stream is not multiplexed");

            await slow;
            await adapters.StopAsync(TickerId, "multiplex", drain: false);
        }

        // ------------------------------------------------------------------ push and ack

        [TestMethod]
        public async Task Pushed_events_are_acknowledged()
        {
            sink.Clear();
            await StartTicker("push", intervalSeconds: 1);

            await WaitFor(() => sink.Delivered.Any(d => d.AdapterId == TickerId),
                TimeSpan.FromSeconds(15), "no event was pushed");

            var delivered = sink.Delivered.First(d => d.AdapterId == TickerId);
            Assert.AreEqual("tick", delivered.Endpoint);
            Assert.IsTrue(delivered.Accepted);
            StringAssert.Contains(delivered.DedupeKey, "ticker:push:");

            await adapters.StopAsync(TickerId, "push", drain: false);
        }

        /// <summary>
        /// Ack ordering, end to end. A rejected event must leave the source untouched so the
        /// broker redelivers it — and the second delivery must carry the same dedupe key, which
        /// is the only thing that makes at-least-once safe.
        /// </summary>
        [TestMethod]
        public async Task A_rejected_event_is_left_for_redelivery_and_then_deduplicated()
        {
            sink.Clear();
            sink.RejectEverything = true;

            await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = FolderId,
                InstanceKey = "redelivery",
                StartupValues =
                {
                    ["Path"] = inbox,
                    ["ArchivePath"] = archive,
                    ["Pattern"] = "*.json",
                    ["PollSeconds"] = "1"
                }
            });

            var file = Path.Combine(inbox, "redelivery.json");
            await File.WriteAllTextAsync(file, "{\"n\":1}");

            await WaitFor(() => sink.Delivered.Any(d => !d.Accepted),
                TimeSpan.FromSeconds(15), "the rejected delivery never happened");

            var rejectedKey = sink.Delivered.First(d => !d.Accepted).DedupeKey;

            Assert.IsTrue(File.Exists(file),
                "a rejected event must NOT be archived — leaving it in place is what makes it redeliver");

            // Now accept: the redelivery should land, and be archived this time.
            sink.RejectEverything = false;

            await WaitFor(() => sink.Delivered.Any(d => d.Accepted && d.DedupeKey == rejectedKey),
                TimeSpan.FromSeconds(15), "the message was never redelivered");

            await WaitFor(() => !File.Exists(file),
                TimeSpan.FromSeconds(10), "an accepted event should have been archived");

            Assert.AreEqual(1, Directory.GetFiles(archive).Length);

            // Same content again is the same dedupe key, so the host recognises it.
            await File.WriteAllTextAsync(Path.Combine(inbox, "again.json"), "{\"n\":1}");
            await WaitFor(() => sink.DuplicateCount > 0,
                TimeSpan.FromSeconds(15), "identical content should have produced the same dedupe key");

            await adapters.StopAsync(FolderId, "redelivery", drain: false);
        }

        // ------------------------------------------------------------------ health

        [TestMethod]
        public async Task Health_reports_both_host_observed_and_adapter_reported_figures()
        {
            var instance = await StartTicker("health");

            await WaitFor(() => adapters.Describe()
                    .Any(h => h.InstanceKey == "health" && h.LastHeartbeatOn.HasValue),
                TimeSpan.FromSeconds(15), "no heartbeat was recorded");

            var health = adapters.Describe().Single(h => h.InstanceKey == "health");

            Assert.IsTrue(health.WorkingSetBytes > 0, "host-observed memory should be sampled");
            Assert.IsTrue(health.ThreadCount > 0, "host-observed thread count should be sampled");
            Assert.IsTrue(health.Connected, "the adapter reports itself connected");
            Assert.IsNotNull(health.ReportedState);
            Assert.IsTrue(health.Details.ContainsKey("intervalSeconds"),
                "provider detail from the heartbeat should reach the health view");

            await adapters.StopAsync(TickerId, "health", drain: false);
        }

        [TestMethod]
        public async Task Runtime_reconfiguration_takes_effect_without_a_restart()
        {
            var instance = await StartTicker("reconfigure");
            var before = adapters.Describe().Single(h => h.InstanceKey == "reconfigure").RestartCount;

            await instance.InvokeAsync<object>("SetInterval", 7);

            await WaitFor(() => adapters.Describe()
                    .Single(h => h.InstanceKey == "reconfigure")
                    .Details.TryGetValue("intervalSeconds", out var v) && v == "7",
                TimeSpan.FromSeconds(15), "the new interval never appeared on the heartbeat");

            Assert.AreEqual(before, adapters.Describe()
                .Single(h => h.InstanceKey == "reconfigure").RestartCount,
                "reconfiguring must not restart the adapter");

            await adapters.StopAsync(TickerId, "reconfigure", drain: false);
        }

        // ------------------------------------------------------------------ helpers

        // ------------------------------------------------------------------ host-held state

        /// <summary>
        /// The property a polling receiver's cursor depends on: state written through the host
        /// outlives the adapter process, so a restart resumes where it left off instead of
        /// replaying from the beginning.
        /// </summary>
        [TestMethod]
        public async Task State_survives_a_restart_of_the_adapter()
        {
            var instance = await StartTicker("state");

            await instance.InvokeAsync<object>("SaveCursor", "2026-09-08T10:00:00Z");

            var before = await instance.InvokeAsync<Dictionary<string, string>>("ReadCursor");
            Assert.AreEqual("2026-09-08T10:00:00Z", before["cursor"]);

            // Same instance key, a brand new process — which is exactly what the supervisor does
            // after a crash.
            var restarted = await adapters.RestartAsync(TickerId, "state", drain: false);

            var after = await restarted.InvokeAsync<Dictionary<string, string>>("ReadCursor");
            Assert.AreEqual("2026-09-08T10:00:00Z", after["cursor"],
                "the cursor is the host's, so a new process must still see it");

            await restarted.InvokeAsync<object>("ClearCursor");
            var cleared = await restarted.InvokeAsync<Dictionary<string, string>>("ReadCursor");
            Assert.IsNull(cleared["cursor"]);

            await adapters.StopAsync(TickerId, "state", drain: false);
        }

        /// <summary>
        /// State belongs to the INSTANCE, not to the adapter. Two data sources served by one
        /// adapter are two connections, and one's cursor read by the other would skip rows.
        /// </summary>
        [TestMethod]
        public async Task State_is_scoped_to_the_instance()
        {
            var first = await StartTicker("state-a");
            var second = await StartTicker("state-b");

            await first.InvokeAsync<object>("SaveCursor", "a");

            var read = await second.InvokeAsync<Dictionary<string, string>>("ReadCursor");
            Assert.IsNull(read["cursor"], "one instance must not see another instance's state");

            await adapters.StopAsync(TickerId, "state-a", drain: false);
            await adapters.StopAsync(TickerId, "state-b", drain: false);
        }

        // ------------------------------------------------------------------ pool keying

        /// <summary>
        /// Two data sources on one adapter id must not share warm processes: the pool captures the
        /// spec of whichever renter created it, credentials included, so sharing a key meant the
        /// second data source silently ran against the first one's system.
        /// </summary>
        [TestMethod]
        public void Pool_key_separates_specs_that_differ_in_configuration()
        {
            var one = new AdapterSpec
            {
                AdapterId = "db.oracle",
                StartupValues = { ["Host"] = "one.example", ["Password"] = "s1" }
            };
            var two = new AdapterSpec
            {
                AdapterId = "db.oracle",
                StartupValues = { ["Host"] = "two.example", ["Password"] = "s2" }
            };
            var alsoOne = new AdapterSpec
            {
                AdapterId = "db.oracle",
                // Same pairs, written in the other order: the key is canonical, so these share.
                StartupValues = { ["Password"] = "s1", ["Host"] = "one.example" }
            };

            Assert.AreNotEqual(ResidentAdapterHost.PoolKeyOf(one), ResidentAdapterHost.PoolKeyOf(two));
            Assert.AreEqual(ResidentAdapterHost.PoolKeyOf(one), ResidentAdapterHost.PoolKeyOf(alsoOne));

            // An explicit key wins, and nothing secret is readable in either form.
            var keyed = new AdapterSpec { AdapterId = "db.oracle", PoolKey = "datasource-7" };
            Assert.AreEqual("db.oracle:datasource-7", ResidentAdapterHost.PoolKeyOf(keyed));
            StringAssert.Contains(ResidentAdapterHost.PoolKeyOf(one), "db.oracle:");
            Assert.IsFalse(ResidentAdapterHost.PoolKeyOf(one).Contains("s1"),
                "the pool key ends up in logs, so it must not carry credentials");
        }

        static Task<ResidentAdapterInstance> StartTicker(string key, int intervalSeconds = 30) =>
            adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = TickerId,
                InstanceKey = key,
                StartupValues = { ["IntervalSeconds"] = intervalSeconds.ToString() }
            });

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
    }
}
