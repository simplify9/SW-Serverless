using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Resident;
using SW.Serverless.UnitTests.Fixtures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// The supervisor under the conditions that used to go wrong: two pools of one adapter, a stop
    /// or a start arriving during a crash backoff, an adapter that ignores a drain, a slow state
    /// store, a newer version published under a running adapter — and the adapter-side SDK's
    /// cancellation, drain and frame-size behaviour.
    /// </summary>
    [TestClass]
    public class ResidentRobustnessTests
    {
        const string TickerId = "robust.ticker";

        static IHost host;
        static IResidentAdapterHost adapters;
        static ICloudFilesService cloudFiles;
        static readonly AttachLog attaches = new();
        static string localRoot;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            localRoot = Path.Combine(Path.GetTempPath(), "swsl-robust", Guid.NewGuid().ToString("N"));

            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l =>
                {
                    l.ClearProviders();
                    l.AddProvider(attaches);
                })
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-robust");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = localRoot;
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddResidentAdapters<TestEventSink, SlowStateStore>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-r{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-r{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(1);
                        o.MissedHeartbeatsBeforeRestart = 2;
                        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
                        o.DrainDeadline = TimeSpan.FromSeconds(3);
                        o.IdleTimeout = TimeSpan.FromSeconds(2);
                    });
                })
                .Build();

            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();
            cloudFiles = host.Services.GetRequiredService<ICloudFilesService>();

            await TestStore.PublishAsync(cloudFiles, TickerId, "SW.Serverless.Samples.Ticker",
                new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) await host.StopAsync();
            host?.Dispose();
            try { Directory.Delete(localRoot, true); } catch { }
        }

        static Task<ResidentAdapterInstance> Start(string key, long softLimit = 0) =>
            adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = TickerId,
                InstanceKey = key,
                StartupValues = { ["IntervalSeconds"] = "60" },
                SoftMemoryLimitBytes = softLimit
            });

        static async Task<bool> Eventually(Func<bool> condition, TimeSpan within)
        {
            var until = DateTime.UtcNow + within;
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                await Task.Delay(200);
            }
            return condition();
        }

        // ------------------------------------------------------------------ pools

        [TestMethod]
        public async Task Two_pools_of_one_adapter_keep_separate_instances()
        {
            static AdapterSpec Spec(string interval) => new()
            {
                AdapterId = TickerId,
                StartupValues = { ["IntervalSeconds"] = interval }
            };

            await using var a = await adapters.RentAsync(Spec("61"));
            await using var b = await adapters.RentAsync(Spec("62"));

            Assert.AreNotEqual(a.Instance.InstanceKey, b.Instance.InstanceKey,
                "two pools of one adapter used to both name their first slot pool-1 and overwrite each other");
            Assert.AreNotEqual(a.Instance.Process.Id, b.Instance.Process.Id);
            Assert.AreSame(a.Instance, adapters.Get(TickerId, a.Instance.InstanceKey));
            Assert.AreSame(b.Instance, adapters.Get(TickerId, b.Instance.InstanceKey));
        }

        [TestMethod]
        public async Task An_idle_pool_shrinks_away()
        {
            string key;
            await using (var lease = await adapters.RentAsync(new AdapterSpec
                         {
                             AdapterId = TickerId,
                             StartupValues = { ["IntervalSeconds"] = "63" }
                         }))
                key = lease.Instance.InstanceKey;

            Assert.IsTrue(await Eventually(() => adapters.Get(TickerId, key) == null, TimeSpan.FromSeconds(15)),
                "a pooled instance idle past the timeout is retired");
        }

        // ------------------------------------------------------------------ restarts

        [TestMethod]
        public async Task A_stop_during_crash_backoff_keeps_it_stopped()
        {
            var instance = await Start("backoff-stop");
            var attachedBefore = attaches.Count("backoff-stop");

            instance.Process.Kill();
            await Task.Delay(300);
            await adapters.StopAsync(TickerId, "backoff-stop", drain: false);

            // The backoff is two seconds; give it time to fire if it was going to.
            await Task.Delay(TimeSpan.FromSeconds(5));

            Assert.AreEqual(attachedBefore, attaches.Count("backoff-stop"),
                "the pending restart must not bring back an adapter that was stopped");
            Assert.IsNull(adapters.Get(TickerId, "backoff-stop"));
        }

        [TestMethod]
        public async Task A_start_during_crash_backoff_does_not_end_up_with_two()
        {
            var instance = await Start("backoff-start");
            instance.Process.Kill();
            await Task.Delay(300);

            var replacement = await Start("backoff-start");
            Assert.AreEqual(InstanceState.Ready, replacement.State);
            var attachedAfterStart = attaches.Count("backoff-start");

            await Task.Delay(TimeSpan.FromSeconds(5));

            Assert.AreEqual(attachedAfterStart, attaches.Count("backoff-start"),
                "the replaced instance's backoff restart must not attach a second process");

            await adapters.StopAsync(TickerId, "backoff-start", drain: false);
        }

        [TestMethod]
        public async Task An_adapter_that_ignores_a_drain_is_killed_at_the_deadline_and_it_is_not_a_crash()
        {
            // A one-byte soft ceiling: the first sample asks it to drain. A long command keeps it
            // from exiting — the SDK waits for running commands — so only the deadline ends it.
            var instance = await Start("drain-deadline", softLimit: 1);
            var pid = instance.Process.Id;
            _ = instance.InvokeAsync<object>("Sleep", 60, timeoutSeconds: 90);

            Assert.IsTrue(await Eventually(() => instance.Process.HasExited, TimeSpan.FromSeconds(15)),
                "a draining adapter that does not exit is killed after the drain deadline");

            Assert.IsTrue(await Eventually(() => adapters.Get(TickerId, "drain-deadline")?.Process?.Id is { } p && p != pid,
                TimeSpan.FromSeconds(15)), "and restarted");
            var health = adapters.Describe().Single(h => h.InstanceKey == "drain-deadline");
            Assert.AreEqual(0, health.RestartCount, "an exit the host asked for is not counted as a crash");

            await adapters.StopAsync(TickerId, "drain-deadline", drain: false);
        }

        [TestMethod]
        public async Task A_restart_after_a_newer_version_is_published_runs_the_newer_one()
        {
            var instance = await Start("republish");
            var installer = host.Services.GetRequiredService<AdapterInstaller>();
            var oldDirectory = (await installer.GetMetadataAsync(TickerId)).Directory;

            await TestStore.PublishAsync(cloudFiles, TickerId, "SW.Serverless.Samples.Ticker",
                new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" }, version: "v2");
            // Metadata is cached for a minute; a test can't wait that out.
            host.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()
                .Remove($"{AdapterInstaller.NamingPrefix}.{TickerId}");
            var newer = await installer.InstallAsync(TickerId);
            Assert.AreNotEqual(oldDirectory, newer.Directory);
            Assert.IsTrue(Directory.Exists(oldDirectory), "the running version's directory is not pruned under it");

            instance.Process.Kill();

            Assert.IsTrue(await Eventually(
                    () => adapters.Get(TickerId, "republish") is { State: InstanceState.Ready } i && !ReferenceEquals(i, instance),
                    TimeSpan.FromSeconds(20)),
                "the restart re-resolves the package instead of reusing a path that may be gone");

            await adapters.StopAsync(TickerId, "republish", drain: false);
        }

        // ------------------------------------------------------------------ state

        [TestMethod]
        public async Task A_slow_state_store_does_not_cost_the_adapter_its_heartbeats()
        {
            var instance = await Start("slow-state");
            var pid = instance.Process.Id;

            // Six seconds against a one-second heartbeat with two allowed misses: handled on the
            // read loop, this got a healthy adapter killed.
            await instance.InvokeAsync<object>("SaveCursor", SlowStateStore.SlowPrefix + "6", timeoutSeconds: 30);

            Assert.AreEqual(pid, adapters.Get(TickerId, "slow-state").Process.Id);
            Assert.IsFalse(instance.Process.HasExited);

            await adapters.StopAsync(TickerId, "slow-state", drain: false);
        }

        // ------------------------------------------------------------------ SDK behaviour

        [TestMethod]
        public async Task A_call_the_host_gives_up_on_is_cancelled_in_the_adapter()
        {
            var instance = await Start("cancel");

            await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                instance.InvokeAsync<object>("Wait", 30, timeoutSeconds: 1));

            Assert.IsTrue(await Eventually(() =>
                    instance.InvokeAsync<Dictionary<string, long>>("GetCancelledCalls").Result["cancelled"] == 1,
                    TimeSpan.FromSeconds(5)),
                "the timed-out command stops instead of running on behind the caller's back");

            await adapters.StopAsync(TickerId, "cancel", drain: false);
        }

        [TestMethod]
        public async Task A_drain_delivers_the_results_of_commands_already_running()
        {
            var instance = await Start("drain-result");
            var running = instance.InvokeAsync<Dictionary<string, int>>("Sleep", 2, timeoutSeconds: 30);
            await Task.Delay(300);

            var stopping = adapters.StopAsync(TickerId, "drain-result", drain: true);
            var result = await running;
            await stopping;

            Assert.AreEqual(2, result["sleptSeconds"]);
        }

        [TestMethod]
        public async Task A_result_too_big_for_a_frame_fails_only_that_call()
        {
            var instance = await Start("huge");
            var pid = instance.Process.Id;

            var error = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(() =>
                instance.InvokeAsync<string>("Huge", 70, timeoutSeconds: 60));
            StringAssert.Contains(error.Message, "the most one call can return");

            Assert.IsNotNull(await instance.InvokeAsync<object>("GetCounters"));
            Assert.AreEqual(pid, instance.Process.Id);

            await adapters.StopAsync(TickerId, "huge", drain: false);
        }

        [TestMethod]
        public async Task Runner_StartupValueOf_works_in_a_resident_adapter()
        {
            var instance = await Start("runner-statics");

            var fromStartup = await instance.InvokeAsync<Dictionary<string, string>>("ReadThroughRunner", "IntervalSeconds");
            Assert.AreEqual("60", fromStartup["value"]);

            var fromCall = await instance.InvokeAsync<Dictionary<string, string>>("ReadThroughRunner", "IntervalSeconds",
                properties: new Dictionary<string, string> { ["IntervalSeconds"] = "5" });
            Assert.AreEqual("5", fromCall["value"]);

            await adapters.StopAsync(TickerId, "runner-statics", drain: false);
        }

        [TestMethod]
        public async Task Health_shows_startup_value_names_but_not_their_values()
        {
            await Start("redacted");

            var health = adapters.Describe().Single(h => h.InstanceKey == "redacted");
            Assert.AreEqual("********", health.StartupValues["IntervalSeconds"]);

            await adapters.StopAsync(TickerId, "redacted", drain: false);
        }

        [TestMethod]
        public void The_host_accepts_a_range_of_protocol_versions()
        {
            Assert.IsTrue(ProtocolVersions.Supports(2));
            Assert.IsFalse(ProtocolVersions.Supports(1));
            Assert.IsFalse(ProtocolVersions.Supports(ProtocolVersions.Max + 1));
        }

        // ------------------------------------------------------------------ fixtures

        /// <summary>A state store that takes its time over values that ask it to.</summary>
        public class SlowStateStore : IAdapterStateStore
        {
            public const string SlowPrefix = "slow:";
            readonly ConcurrentDictionary<string, string> values = new();

            public Task<string> GetAsync(AdapterStateKey key, CancellationToken cancellationToken) =>
                Task.FromResult(values.TryGetValue(key.ToString(), out var v) ? v : null);

            public async Task SetAsync(AdapterStateKey key, string value, CancellationToken cancellationToken)
            {
                if (value != null && value.StartsWith(SlowPrefix) && int.TryParse(value[SlowPrefix.Length..], out var seconds))
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
                if (value == null) values.TryRemove(key.ToString(), out _);
                else values[key.ToString()] = value;
            }
        }

        /// <summary>Counts "attached" log lines per instance key: one per process that came up.</summary>
        public class AttachLog : ILoggerProvider
        {
            static readonly Regex Attached = new(@"Adapter (?<id>[^/]+)/(?<key>\S+) attached\.");
            readonly ConcurrentDictionary<string, int> counts = new();

            public int Count(string instanceKey) => counts.TryGetValue(instanceKey, out var n) ? n : 0;

            public ILogger CreateLogger(string categoryName) => new Logger(this);
            public void Dispose() { }

            sealed class Logger(AttachLog log) : ILogger
            {
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    var match = Attached.Match(formatter(state, exception) ?? "");
                    if (match.Success) log.counts.AddOrUpdate(match.Groups["key"].Value, 1, (_, n) => n + 1);
                }
            }
        }
    }
}
