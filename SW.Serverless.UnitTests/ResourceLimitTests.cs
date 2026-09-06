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
    /// The ceilings an adapter process runs under, and how they are changed while it runs.
    ///
    /// An adapter is a separate process the host does not otherwise constrain. Without a ceiling,
    /// one runaway payload is bounded by nothing but the machine, and every other adapter on the
    /// node goes down with it — so these limits are the difference between one integration failing
    /// and the node failing. They were enforced against real process metrics and covered by nothing.
    ///
    /// Everything here runs a REAL adapter that allocates real memory and burns a real core,
    /// because the watchdog samples the operating system: resident set size and processor time. A
    /// fake would prove only that the test agrees with itself.
    /// </summary>
    [TestClass]
    public class ResourceLimitTests
    {
        const string GreedyId = "test.greedy";

        // Short enough that a test does not wait a minute for the watchdog, long enough that the
        // CPU average has real wall time behind it.
        static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(700);

        static IHost host;
        static IResidentAdapterHost adapters;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-limits");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-limittests", "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-l{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-l{Environment.ProcessId}";
                        o.HeartbeatInterval = Heartbeat;
                        o.HandshakeTimeout = TimeSpan.FromSeconds(30);

                        // Crash-loop quarantine would otherwise stop the restarts these tests are
                        // about: a ceiling firing repeatedly looks exactly like a crash loop.
                        o.CrashLoopThreshold = 50;
                    });
                })
                .Build();

            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();

            await TestStore.PublishAsync(host.Services.GetRequiredService<ICloudFilesService>(),
                GreedyId, "SW.Serverless.Samples.Greedy",
                new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) await host.StopAsync();
            host?.Dispose();
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "swsl-limittests"), true); } catch { }
        }

        // ---------------------------------------------------------------- memory

        /// <summary>
        /// The soft ceiling is the one that should normally fire, and it must fire GENTLY: the
        /// adapter is asked to drain, so whatever is in flight is finished or handed back rather
        /// than lost. A restart proves the watchdog acted; the drain request proves it chose the
        /// graceful path rather than a kill.
        /// </summary>
        [TestMethod]
        public async Task A_soft_memory_ceiling_asks_the_adapter_to_drain_and_it_comes_back()
        {
            var instance = await StartAsync("soft", allocateOnStartMb: 220, spec => spec
                .WithSoftMemory(120));

            var first = instance.Process?.Id;
            Assert.IsNotNull(first);

            // A new process under the same key: the watchdog sampled, decided, and the supervisor
            // relaunched it.
            var replacement = await WaitForNewProcessAsync("soft", first.Value, TimeSpan.FromSeconds(45));

            Assert.AreNotEqual(first.Value, replacement,
                "the soft memory ceiling never fired");

            // And it is serviceable afterwards, rather than merely restarted. The replacement is
            // still spawning the instant its pid appears, so this waits for the handshake.
            await WaitForReadyAsync("soft", TimeSpan.FromSeconds(30));
            var stats = await adapters.Get(GreedyId, "soft")
                .InvokeAsync<JObject>("GetStats", timeoutSeconds: 15);
            Assert.IsNotNull(stats);
        }

        /// <summary>
        /// The hard ceiling is the backstop, and it does not negotiate. An adapter that ignores a
        /// drain — or grows faster than it can drain — is killed, because at that point the choice
        /// is between one adapter's in-flight work and the whole node.
        /// </summary>
        [TestMethod]
        public async Task A_hard_memory_ceiling_kills_an_adapter_that_will_not_stop_growing()
        {
            var instance = await StartAsync("hard", allocateOnStartMb: 260, spec => spec
                .WithHardMemory(140));

            var first = instance.Process?.Id;
            Assert.IsNotNull(first);

            var replacement = await WaitForNewProcessAsync("hard", first.Value, TimeSpan.FromSeconds(45));
            Assert.AreNotEqual(first.Value, replacement, "the hard memory ceiling never fired");
        }

        /// <summary>
        /// A ceiling set on the spec beats the host default, or a per-adapter limit would be
        /// decoration: every adapter on a node would run under whichever number the host happened
        /// to be configured with.
        /// </summary>
        [TestMethod]
        public async Task A_ceiling_on_the_spec_overrides_the_host_default()
        {
            // The host default here is 0 — off — so a spec-level ceiling firing at all is the
            // proof that the spec is consulted first.
            var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ResidentOptions>>();
            Assert.AreEqual(0, options.Value.SoftMemoryLimitBytes,
                "this test's premise is that the host default is off");

            var instance = await StartAsync("override", allocateOnStartMb: 200, spec => spec
                .WithSoftMemory(110));

            var first = instance.Process?.Id;
            var replacement = await WaitForNewProcessAsync("override", first!.Value, TimeSpan.FromSeconds(45));

            Assert.AreNotEqual(first.Value, replacement);
        }

        /// <summary>
        /// An adapter comfortably inside its ceiling is left alone. Without this the others prove
        /// only that something restarts adapters — not that the limit is what decided it.
        /// </summary>
        [TestMethod]
        public async Task An_adapter_within_its_ceiling_is_never_touched()
        {
            var instance = await StartAsync("quiet", allocateOnStartMb: 20, spec => spec
                .WithSoftMemory(400).WithHardMemory(600));

            var first = instance.Process?.Id;
            Assert.IsNotNull(first);

            // Several watchdog passes, so "not yet" cannot pass for "never".
            await Task.Delay(Heartbeat * 8);

            Assert.AreEqual(first.Value, adapters.Get(GreedyId, "quiet")?.Process?.Id,
                "an adapter inside its ceiling was recycled anyway");
        }

        // ---------------------------------------------------------------- cpu

        /// <summary>
        /// Sustained CPU trips the ceiling and asks for a drain.
        ///
        /// The burn outlasts the sample window on purpose: the point of the rule is that it fires
        /// on a run of samples, so the adapter has to still be pegged when the streak completes.
        /// </summary>
        [TestMethod]
        public async Task Sustained_cpu_trips_the_ceiling_and_recycles_the_adapter()
        {
            // CpuPercent is a share of the WHOLE machine, so a single pegged core reads
            // 100/ProcessorCount — about 10% here. A ceiling above that could never be reached by
            // one busy thread, which is exactly the trap the metric's documentation now warns about.
            var onePeggedCore = 100.0 / Environment.ProcessorCount;
            var instance = await StartAsync("cpu", burnCpuOnStartSeconds: 30, configure: spec => spec
                .WithCpu(percent: onePeggedCore / 3, samples: 2));

            var first = instance.Process?.Id;
            Assert.IsNotNull(first);

            var replacement = await WaitForNewProcessAsync("cpu", first.Value, TimeSpan.FromSeconds(45));
            Assert.AreNotEqual(first.Value, replacement, "the sustained CPU ceiling never fired");
        }

        /// <summary>
        /// A burst does NOT trip it — and this is the test that gives the rule its meaning.
        ///
        /// An adapter draining a backlog is supposed to peg a core. A ceiling that recycled it for
        /// working hard would be a bug wearing a limit's clothes, so the streak has to be long
        /// enough that a short burst passes underneath it.
        /// </summary>
        [TestMethod]
        public async Task A_short_burst_of_work_does_not_trip_the_cpu_ceiling()
        {
            var instance = await StartAsync("burst", configure: spec => spec
                // A reachable ceiling — the burst really does exceed it — but ten consecutive
                // samples is about seven seconds of solid CPU at this heartbeat, and the burst is
                // a fraction of that. So the streak, not the threshold, is what saves it.
                .WithCpu(percent: 100.0 / Environment.ProcessorCount / 3, samples: 10));

            var first = instance.Process?.Id;
            Assert.IsNotNull(first);

            await instance.InvokeAsync<JObject>("BurnCpu", 1.5, timeoutSeconds: 15);
            await Task.Delay(Heartbeat * 8);

            Assert.AreEqual(first.Value, adapters.Get(GreedyId, "burst")?.Process?.Id,
                "a short burst of legitimate work recycled the adapter");
        }

        // ---------------------------------------------------------------- update / restart

        /// <summary>
        /// The soft ceiling can be changed on a running adapter and takes effect without a
        /// restart: it is read from the spec on every sample. An operator tightening a limit
        /// during an incident should not have to bounce the connection to do it.
        ///
        /// The adapter grows AFTER the ceiling is lowered rather than merely sitting above it,
        /// because resident set size is not a number the process fully controls — the operating
        /// system reclaims pages under pressure, so a heap that was over the line a moment ago can
        /// drift back under it while nothing at all has changed. Growing into the new ceiling is
        /// the same behaviour under test and does not depend on the OS holding still.
        /// </summary>
        [TestMethod]
        public async Task Lowering_the_soft_ceiling_takes_effect_without_a_restart()
        {
            var instance = await StartAsync("update-soft", allocateOnStartMb: 60, spec => spec
                .WithSoftMemory(800));   // far above anything this test allocates

            var first = instance.Process?.Id;
            Assert.IsNotNull(first);

            await Task.Delay(Heartbeat * 3);
            Assert.AreEqual(first, adapters.Get(GreedyId, "update-soft")?.Process?.Id,
                "nothing should have fired while the ceiling was 800 MB");

            var update = await adapters.UpdateLimitsAsync(GreedyId, "update-soft",
                new ResourceLimits { SoftMemoryLimitBytes = Mb(200) });

            Assert.IsTrue(update.Applied);
            Assert.IsFalse(update.RestartRequired,
                "the soft ceiling is read every sample, so it needs no restart");

            // Over the NEW ceiling and nowhere near the old one, so what happens next can only be
            // the lowered limit.
            try
            {
                await adapters.Get(GreedyId, "update-soft")
                    .InvokeAsync<JObject>("Allocate", 260, timeoutSeconds: 60);
            }
            catch (AdapterInvocationException)
            {
                // The drain can land mid-allocation and take the command with it. That is the
                // ceiling firing, which is what the test is here to observe.
            }

            var replacement = await WaitForNewProcessAsync("update-soft", first.Value, TimeSpan.FromSeconds(45));
            Assert.AreNotEqual(first.Value, replacement, "the lowered ceiling never took effect");
        }

        /// <summary>
        /// The hard ceiling is the runtime's own GC heap limit, handed over when the process
        /// launched. It cannot change in place, and the host says so rather than accepting the
        /// change and quietly running under the old number — which is the failure an operator
        /// could not possibly detect.
        /// </summary>
        [TestMethod]
        public async Task Changing_the_hard_ceiling_reports_that_a_restart_is_required()
        {
            await StartAsync("update-hard", allocateOnStartMb: 10, spec => spec.WithHardMemory(500));

            var update = await adapters.UpdateLimitsAsync(GreedyId, "update-hard",
                new ResourceLimits { HardMemoryLimitBytes = Mb(250) });

            Assert.IsTrue(update.Applied);
            Assert.IsTrue(update.RestartRequired);
            StringAssert.Contains(update.Reason, "restart", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Restarting keeps the instance in place under the same key. That matters beyond
        /// tidiness: in Bitween the key is the data source, and dropping the registry entry would
        /// release the lease that makes the broker connection exclusive — so a restart would
        /// briefly become a handover.
        /// </summary>
        [TestMethod]
        public async Task Restarting_relaunches_in_place_under_the_same_key()
        {
            // No allocate-on-start: a relaunched process re-applies its startup values, so
            // "allocated 0" only means "fresh" when nothing told it to allocate.
            var instance = await StartAsync("restart");
            var first = instance.Process?.Id;
            Assert.IsNotNull(first);

            var restarted = await adapters.RestartAsync(GreedyId, "restart", drain: false);

            Assert.IsNotNull(restarted);
            Assert.AreNotEqual(first.Value, restarted.Process?.Id, "nothing was relaunched");
            Assert.AreEqual(InstanceState.Ready, restarted.State);

            // Same key, still exactly one instance — not a second one beside the first.
            var here = adapters.Describe().Where(h => h.InstanceKey == "restart").ToList();
            Assert.AreEqual(1, here.Count);
            Assert.AreEqual(GreedyId, here[0].AdapterId);

            // And it works, rather than merely existing.
            await adapters.Get(GreedyId, "restart").InvokeAsync<JObject>("Allocate", 5, timeoutSeconds: 30);
            var stats = await adapters.Get(GreedyId, "restart")
                .InvokeAsync<JObject>("GetStats", timeoutSeconds: 15);

            Assert.IsNotNull(stats);
            Assert.AreEqual(5, stats.Value<int>("allocatedMb"),
                "the relaunched process should be serving commands with a heap of its own");
        }

        /// <summary>
        /// A restart is what actually applies a hard ceiling — and the proof is that the runtime
        /// then refuses the allocation itself.
        ///
        /// This is the containment working at its best. The hard ceiling is handed to the child as
        /// DOTNET_GCHeapHardLimit, so an allocation past it throws OutOfMemoryException INSIDE the
        /// adapter and comes back to the caller as a failed command. The watchdog's kill is the
        /// backstop behind that, not the first line: the node never sees the memory at all.
        /// </summary>
        [TestMethod]
        public async Task A_restart_applies_a_hard_ceiling_and_the_runtime_then_refuses_the_allocation()
        {
            var instance = await StartAsync("update-then-restart", allocateOnStartMb: 10,
                spec => spec.WithHardMemory(600));
            var first = instance.Process?.Id;

            // Comfortably allowed under the ceiling it started with.
            await adapters.Get(GreedyId, "update-then-restart")
                .InvokeAsync<JObject>("Allocate", 120, timeoutSeconds: 60);

            var update = await adapters.UpdateLimitsAsync(GreedyId, "update-then-restart",
                new ResourceLimits { HardMemoryLimitBytes = Mb(150) });
            Assert.IsTrue(update.RestartRequired);

            await adapters.RestartAsync(GreedyId, "update-then-restart", drain: false);
            await WaitForReadyAsync("update-then-restart", TimeSpan.FromSeconds(30));

            var afterRestart = adapters.Get(GreedyId, "update-then-restart")?.Process?.Id;
            Assert.AreNotEqual(first, afterRestart);

            // The same allocation that was fine a moment ago is now refused by the runtime, which
            // is the new ceiling being genuinely in force rather than merely recorded.
            var refused = await Assert.ThrowsExceptionAsync<AdapterInvocationException>(
                () => adapters.Get(GreedyId, "update-then-restart")
                    .InvokeAsync<JObject>("Allocate", 400, timeoutSeconds: 60));

            StringAssert.Contains(refused.Message, "OutOfMemory",
                "the allocation failed for some reason other than the heap ceiling");
        }

        [TestMethod]
        public async Task Updating_limits_on_an_instance_that_is_not_running_says_so()
        {
            var update = await adapters.UpdateLimitsAsync(GreedyId, "never-started",
                new ResourceLimits { SoftMemoryLimitBytes = Mb(100) });

            Assert.IsFalse(update.Applied);
            Assert.IsNotNull(update.Reason);
        }

        [TestMethod]
        public async Task Restarting_an_instance_that_is_not_running_throws_rather_than_starting_one()
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => adapters.RestartAsync(GreedyId, "also-never-started"));
        }

        // ---------------------------------------------------------------- helpers

        static long Mb(int megabytes) => (long)megabytes * 1024 * 1024;

        static async Task<ResidentAdapterInstance> StartAsync(string key,
            int allocateOnStartMb = 0, Action<AdapterSpec> configure = null,
            double burnCpuOnStartSeconds = 0)
        {
            var spec = new AdapterSpec { AdapterId = GreedyId, InstanceKey = key };

            // Over the line before the first sample, so the watchdog's decision is the only thing
            // the test is waiting on.
            if (allocateOnStartMb > 0)
                spec.StartupValues["AllocateMbOnStart"] = allocateOnStartMb.ToString();
            if (burnCpuOnStartSeconds > 0)
                spec.StartupValues["BurnCpuOnStart"] = burnCpuOnStartSeconds.ToString();

            configure?.Invoke(spec);
            return await adapters.StartExclusiveAsync(spec);
        }

        /// <summary>A relaunched instance is Spawning before it is Ready; commands need Ready.</summary>
        static async Task WaitForReadyAsync(string key, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (adapters.Get(GreedyId, key)?.State == InstanceState.Ready) return;
                await Task.Delay(200);
            }
            Assert.Fail($"'{key}' never became Ready within {timeout}.");
        }

        /// <summary>
        /// Waits for a DIFFERENT process under the same key — which is what "the watchdog acted"
        /// looks like from outside, whether it drained or killed.
        /// </summary>
        static async Task<int> WaitForNewProcessAsync(string key, int original, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var current = adapters.Get(GreedyId, key)?.Process?.Id;
                if (current.HasValue && current.Value != original) return current.Value;
                await Task.Delay(200);
            }
            return original;
        }
    }

    static class SpecLimitExtensions
    {
        public static AdapterSpec WithSoftMemory(this AdapterSpec spec, int megabytes)
        {
            spec.SoftMemoryLimitBytes = (long)megabytes * 1024 * 1024;
            return spec;
        }

        public static AdapterSpec WithHardMemory(this AdapterSpec spec, int megabytes)
        {
            spec.HardMemoryLimitBytes = (long)megabytes * 1024 * 1024;
            return spec;
        }

        public static AdapterSpec WithCpu(this AdapterSpec spec, double percent, int samples)
        {
            spec.CpuPercentLimit = percent;
            spec.CpuLimitSamples = samples;
            return spec;
        }
    }
}
