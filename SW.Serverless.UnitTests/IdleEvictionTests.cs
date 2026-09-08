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
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// Edge cases for pool idle-eviction (<see cref="ResidentOptions.IdleTimeout"/>,
    /// <see cref="AdapterPool.EvictIdleAsync"/>) that CarrierAdapterTests' single "basic eviction
    /// happens" case does not cover: the host-level default, the 0-means-"use the default"
    /// override rule, cancelling an eviction by renting again, exclusive instances being immune,
    /// and partial eviction when a pool holds more than one idle instance.
    ///
    /// The carrier sample fails its upstream ping softly (state goes "Disconnected", it does not
    /// throw — see CarrierHandler.StartAsync), so none of this needs the gRPC fake CarrierAdapterTests
    /// runs; a plain BaseUrl that nothing is listening on is enough to reach Ready.
    /// </summary>
    [TestClass]
    public class IdleEvictionTests
    {
        const string AdapterId = "test.idle-eviction";

        static IHost host;
        static IResidentAdapterHost adapters;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-idle");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-idletests", "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-ie{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-ie{Environment.ProcessId}";
                        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
                        // Fast enough that every test below finishes in single-digit seconds; the
                        // sweep runs on this same cadence (ResidentAdapterHost.SuperviseAsync).
                        o.HeartbeatInterval = TimeSpan.FromSeconds(1);
                        // The host-level default under test in several cases below. Each test uses
                        // its own AdapterId, so a per-adapter override never leaks between them —
                        // AdapterPool captures whatever AdapterValues came in on the FIRST RentAsync
                        // for an adapter id, and every later caller for that id shares that pool.
                        o.IdleTimeout = TimeSpan.FromSeconds(1);
                    });
                })
                .Build();

            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();

            // One adapter id per test, all backed by the same package, all pointed at a BaseUrl
            // nothing answers — CarrierHandler.StartAsync swallows that and still reaches Ready.
            var cloudFiles = host.Services.GetRequiredService<ICloudFilesService>();
            foreach (var id in new[]
                     {
                         AdapterId + ".global-default",
                         AdapterId + ".zero-override",
                         AdapterId + ".reuse",
                         AdapterId + ".exclusive",
                         AdapterId + ".partial",
                         AdapterId + ".disabled",
                     })
            {
                await TestStore.PublishAsync(cloudFiles, id, "SW.Serverless.Samples.Carrier",
                    new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });
            }
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) { await host.StopAsync(); host.Dispose(); }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "swsl-idletests"), true); } catch { }
        }

        static AdapterSpec Spec(string idSuffix, IDictionary<string, string> adapterValues = null) => new()
        {
            AdapterId = AdapterId + idSuffix,
            StartupValues = { ["BaseUrl"] = "http://localhost:1", ["TimeoutSeconds"] = "1" },
            AdapterValues = adapterValues ?? new Dictionary<string, string> { ["Poolable"] = "true" }
        };

        // ------------------------------------------------------------------

        /// <summary>
        /// An adapter that never mentions IdleTimeoutSeconds still gets evicted once it outlives
        /// the host's own <see cref="ResidentOptions.IdleTimeout"/> — the per-adapter override is
        /// optional, not required to opt into the feature at all.
        /// </summary>
        [TestMethod]
        public async Task An_adapter_with_no_override_uses_the_hosts_default_idle_timeout()
        {
            int pid;
            await using (var lease = await adapters.RentAsync(Spec(".global-default")))
                pid = lease.Instance.Process.Id;

            // 1s default + at least one 1s sweep, with slack for scheduling jitter.
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.IsFalse(adapters.Describe().Any(h => h.ProcessId == pid),
                "the host's default idle timeout should have evicted this instance even without a per-adapter override");
        }

        /// <summary>
        /// AdapterPool.IdleTimeoutFor only honours a POSITIVE override ("seconds > 0"); an explicit
        /// "0" is not a way to disable eviction per-adapter, it just falls through to whatever the
        /// host default is. If this ever changed to mean "disabled", it would be a silent
        /// footgun — an adapter author writing "0" meaning "off" would instead inherit the host's
        /// timeout, however short.
        /// </summary>
        [TestMethod]
        public async Task An_explicit_zero_override_falls_back_to_the_hosts_default_instead_of_disabling_eviction()
        {
            var spec = Spec(".zero-override",
                new Dictionary<string, string> { ["Poolable"] = "true", ["IdleTimeoutSeconds"] = "0" });

            int pid;
            await using (var lease = await adapters.RentAsync(spec))
                pid = lease.Instance.Process.Id;

            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.IsFalse(adapters.Describe().Any(h => h.ProcessId == pid),
                "\"IdleTimeoutSeconds\": \"0\" must fall back to the host default (non-zero here), not disable eviction");
        }

        /// <summary>
        /// Checking an instance back out clears IdleSince (AdapterPool.RentAsync), so a caller that
        /// keeps renting before the timeout elapses keeps its warm process indefinitely — the
        /// eviction clock only starts counting from the MOST RECENT check-in.
        /// </summary>
        [TestMethod]
        public async Task Renting_again_before_the_timeout_elapses_keeps_the_instance_warm()
        {
            var spec = Spec(".reuse",
                new Dictionary<string, string> { ["Poolable"] = "true", ["IdleTimeoutSeconds"] = "2" });

            int firstPid;
            await using (var lease = await adapters.RentAsync(spec))
                firstPid = lease.Instance.Process.Id;

            // Well under the 2s timeout, and past at least one sweep, so this proves the sweep ran
            // and chose not to evict rather than merely not having had a chance to yet.
            await Task.Delay(TimeSpan.FromSeconds(1));

            int secondPid;
            await using (var lease = await adapters.RentAsync(spec))
                secondPid = lease.Instance.Process.Id;

            Assert.AreEqual(firstPid, secondPid, "renting again before the timeout should reuse the warm process");

            // Now let it actually sit idle past the timeout, from THIS check-in.
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.IsFalse(adapters.Describe().Any(h => h.ProcessId == firstPid),
                "once nothing rents it again, the instance should still be evicted on its own schedule");
        }

        /// <summary>
        /// EvictIdleAsync only ever walks the host's pools — exclusive instances (broker
        /// connections, StartExclusiveAsync) are never in a pool's idle bag, so a host-wide idle
        /// timeout must never retire one, no matter how long it sits unused.
        /// </summary>
        [TestMethod]
        public async Task Exclusive_instances_are_never_evicted_by_the_idle_sweep()
        {
            var instance = await adapters.StartExclusiveAsync(Spec(".exclusive"));
            var pid = instance.Process.Id;

            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.IsTrue(adapters.Describe().Any(h => h.ProcessId == pid && h.State == InstanceState.Ready),
                "an exclusive instance must survive the same idle window that would evict a pooled one");

            await adapters.StopAsync(AdapterId + ".exclusive", "default", drain: false);
        }

        /// <summary>
        /// A pool can hold several idle instances at once, checked in at different times. Eviction
        /// must retire only the ones actually past the timeout on a given sweep, not the whole
        /// idle set — otherwise a busy pool would thrash down to zero the moment ANY one instance
        /// goes stale.
        /// </summary>
        [TestMethod]
        public async Task Only_the_instance_past_its_own_timeout_is_retired_not_the_whole_pool()
        {
            var spec = Spec(".partial",
                new Dictionary<string, string> { ["Poolable"] = "true", ["PoolSize"] = "2", ["IdleTimeoutSeconds"] = "3" });

            // Two concurrent rents against an empty pool spawn two separate instances.
            var leaseA = await adapters.RentAsync(spec);
            var leaseB = await adapters.RentAsync(spec);
            var pidA = leaseA.Instance.Process.Id;
            var pidB = leaseB.Instance.Process.Id;
            Assert.AreNotEqual(pidA, pidB, "two concurrent rents on an empty pool must not share a process");

            await leaseA.DisposeAsync(); // A's idle clock starts now (t=0)
            await Task.Delay(TimeSpan.FromSeconds(2));
            await leaseB.DisposeAsync(); // B's idle clock starts two seconds later (t=2)

            // At t≈4: A is 4s idle (past its 3s timeout) and evicted; B is 2s idle (still under it).
            await Task.Delay(TimeSpan.FromSeconds(2));

            var midway = adapters.Describe();
            Assert.IsFalse(midway.Any(h => h.ProcessId == pidA), "A should already be past its timeout and retired");
            Assert.IsTrue(midway.Any(h => h.ProcessId == pidB && h.State == InstanceState.Ready),
                "B checked in two seconds after A, so it should still be within its own timeout");

            // At t≈6: B is now past its 3s timeout too.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.IsFalse(adapters.Describe().Any(h => h.ProcessId == pidB),
                "B should eventually be retired on its own schedule once it, too, sits idle long enough");
        }

        /// <summary>
        /// A per-adapter override of 0 falls back to the host default (see the "zero override"
        /// test above) — so to prove eviction can genuinely be switched off, the HOST itself must
        /// be configured with IdleTimeout = TimeSpan.Zero. That is a different host from the one
        /// this class shares (whose default is 1s), built here with its own short-lived scope.
        /// </summary>
        [TestMethod]
        public async Task Idle_timeout_of_zero_on_the_host_disables_eviction_entirely()
        {
            using var disabledHost = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-idle-disabled");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-idletests-disabled", "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-ied{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-ied{Environment.ProcessId}";
                        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
                        o.HeartbeatInterval = TimeSpan.FromSeconds(1);
                        // The default in production too (ResidentOptions.IdleTimeout = TimeSpan.Zero):
                        // eviction is opt-in, so a host that never configures it must never evict.
                    });
                })
                .Build();

            await disabledHost.StartAsync();
            try
            {
                var disabledAdapters = disabledHost.Services.GetRequiredService<IResidentAdapterHost>();
                var cloudFiles = disabledHost.Services.GetRequiredService<ICloudFilesService>();
                await TestStore.PublishAsync(cloudFiles, AdapterId + ".disabled", "SW.Serverless.Samples.Carrier",
                    new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });

                int pid;
                await using (var lease = await disabledAdapters.RentAsync(Spec(".disabled")))
                    pid = lease.Instance.Process.Id;

                // Several sweeps' worth of idling — with the default disabled, none of them should act.
                await Task.Delay(TimeSpan.FromSeconds(4));

                Assert.IsTrue(disabledAdapters.Describe().Any(h => h.ProcessId == pid && h.State == InstanceState.Ready),
                    "IdleTimeout = TimeSpan.Zero (the default) must never evict, however long an instance sits idle");
            }
            finally
            {
                await disabledHost.StopAsync();
            }
        }
    }
}
