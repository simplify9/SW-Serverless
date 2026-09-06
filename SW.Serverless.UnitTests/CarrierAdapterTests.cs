using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Resident;
using SW.Serverless.Samples.CarrierContract;
using SW.Serverless.UnitTests.Fixtures;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// A typical adapter — DI, options, a gRPC upstream, retries — called the way the host calls a
    /// classic adapter. A real gRPC service runs on a loopback port so the calls are genuine.
    /// </summary>
    [TestClass]
    public class CarrierAdapterTests
    {
        const string AdapterId = "test.carrier";

        static WebApplication carrier;
        static IHost host;
        static IResidentAdapterHost adapters;
        static string baseUrl;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            // Port 0 lets the OS hand out a free port and hold it for Kestrel. Picking a random
            // number instead reserves nothing, so a parallel test or any local process can take it
            // between the choice and the bind — an intermittent AddressAlreadyInUse.
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
                k.Listen(System.Net.IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();

            carrier = builder.Build();
            carrier.MapGrpcService<FakeCarrier>();
            await carrier.StartAsync();

            baseUrl = carrier.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First()
                .Replace("127.0.0.1", "localhost");

            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-carrier");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-carriertests", "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-c{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-c{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(3);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(45);
                    });
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                })
                .Build();

            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();

            await TestStore.PublishAsync(host.Services.GetRequiredService<ICloudFilesService>(),
                AdapterId, "SW.Serverless.Samples.Carrier",
                new Dictionary<string, string>
                {
                    ["Protocol"] = "2", ["Lifecycle"] = "resident",
                    ["Poolable"] = "true", ["PoolSize"] = "2"
                });
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) { await host.StopAsync(); host.Dispose(); }
            if (carrier != null) { await carrier.StopAsync(); await carrier.DisposeAsync(); }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "swsl-carriertests"), true); } catch { }
        }

        [TestCleanup]
        public async Task TestCleanup()
        {
            foreach (var health in adapters.Describe())
                await adapters.StopAsync(health.AdapterId, health.InstanceKey, drain: false);
        }

        static AdapterSpec Spec(string key = null) => new()
        {
            AdapterId = AdapterId,
            InstanceKey = key,
            StartupValues =
            {
                ["BaseUrl"] = baseUrl,
                ["Account"] = "TEST-ACCT",
                ["ApiKey"] = "not-a-real-secret",
                ["MaxAttempts"] = "3",
                ["RetryDelayMs"] = "50",
                ["TimeoutSeconds"] = "10"
            },
            AdapterValues = { ["Poolable"] = "true", ["PoolSize"] = "2" }
        };

        // ------------------------------------------------------------------

        /// <summary>
        /// The adapter cannot start unless the container resolved IOptions, the gRPC client from
        /// the factory, the CallLog and an ILogger — and it cannot report Connected unless the
        /// upstream Ping actually succeeded over the wire.
        /// </summary>
        [TestMethod]
        public async Task It_connects_to_its_upstream_using_injected_configuration()
        {
            var instance = await adapters.StartExclusiveAsync(Spec("connect"));

            var result = await instance.InvokeAsync<JObject>("TestConnection");

            Assert.IsTrue(result.Value<bool>("ok"));
            Assert.AreEqual(baseUrl, result.Value<string>("endpoint"));
            Assert.AreEqual("fake-carrier/1.0", result.Value<string>("version"));
        }

        [TestMethod]
        public async Task The_host_calls_it_exactly_as_it_calls_a_classic_adapter()
        {
            var instance = await adapters.StartExclusiveAsync(Spec("classic-shape"));

            var result = await instance.InvokeAsync<JObject>("CreateShipment", new
            {
                Reference = "SO-1",
                Pieces = new[] { new { Weight = 2.0, Length = 10, Width = 10, Height = 10 } }
            });

            Assert.IsTrue(result.Value<bool>("Succeeded"));
            StringAssert.StartsWith(result.Value<string>("TrackingNumber"), "FK");
            Assert.IsTrue(result.Value<decimal>("Price") > 0);
        }

        /// <summary>
        /// A carrier saying no is a business outcome. Turning it into an exception would make a
        /// rejected shipment look like an adapter outage — the mistake a third of the Traxis
        /// adapters make by deriving from raw AdapterBase.
        /// </summary>
        [TestMethod]
        public async Task A_carrier_rejection_is_a_result_and_not_an_exception()
        {
            var instance = await adapters.StartExclusiveAsync(Spec("rejection"));

            var result = await instance.InvokeAsync<JObject>("CreateShipment", new
            {
                Reference = "SO-HEAVY",
                Pieces = new[] { new { Weight = 40.0, Length = 10, Width = 10, Height = 10 } }
            });

            Assert.IsFalse(result.Value<bool>("Succeeded"));
            Assert.AreEqual("PARCEL_TOO_HEAVY", result.Value<string>("ErrorCode"));
            Assert.AreEqual(InstanceState.Ready, instance.State, "the adapter must still be healthy");
        }

        [TestMethod]
        public async Task Transient_upstream_failures_are_retried()
        {
            FakeCarrier.FailNextCalls = 2;
            var instance = await adapters.StartExclusiveAsync(Spec("retry"));

            var result = await instance.InvokeAsync<JObject>("CreateShipment", new
            {
                Reference = "SO-RETRY",
                Pieces = new[] { new { Weight = 1.0, Length = 10, Width = 10, Height = 10 } }
            }, timeoutSeconds: 30);

            Assert.IsTrue(result.Value<bool>("Succeeded"), "two transient failures should be retried through");
            Assert.AreEqual(0, FakeCarrier.FailNextCalls);
        }

        // ------------------------------------------------------------------ the pooled shape

        /// <summary>
        /// The Traxis pattern: call a command, then GetLogs, and expect the command's upstream
        /// calls back. Both invocations must land in the SAME session, which is what the lease's
        /// session id is for — without it GetLogs returns empty and the audit trail is lost.
        /// </summary>
        [TestMethod]
        public async Task GetLogs_after_a_command_sees_that_commands_calls()
        {
            await using var lease = await adapters.RentAsync(Spec());

            await lease.InvokeAsync<JObject>("CreateShipment", new
            {
                Reference = "SO-LOGS",
                Pieces = new[] { new { Weight = 1.0, Length = 10, Width = 10, Height = 10 } }
            });

            var logs = await lease.InvokeAsync<JObject>("GetLogs");

            Assert.AreEqual(lease.SessionId, logs.Value<string>("SessionId"));

            var entries = logs["Entries"].ToArray();
            Assert.AreEqual(1, entries.Length, "the CreateShipment call should be in this session's log");
            Assert.AreEqual("CreateShipment", entries[0].Value<string>("Operation"));
            Assert.IsTrue(entries[0].Value<bool>("Succeeded"));
        }

        /// <summary>
        /// The LargeFiles of the pooling story: one caller's audit trail must never appear in the
        /// next caller's, even when they share a warm process. Traxis's process-static LogStore
        /// would fail this outright.
        /// </summary>
        [TestMethod]
        public async Task One_leases_call_log_never_leaks_into_another()
        {
            string firstSession;

            await using (var first = await adapters.RentAsync(Spec()))
            {
                firstSession = first.SessionId;
                await first.InvokeAsync<JObject>("CreateShipment", new
                {
                    Reference = "SO-FIRST",
                    Pieces = new[] { new { Weight = 1.0, Length = 10, Width = 10, Height = 10 } }
                });

                Assert.AreEqual(1, (await first.InvokeAsync<JObject>("GetLogs"))["Entries"].Count());
            }

            await using var second = await adapters.RentAsync(Spec());

            Assert.AreNotEqual(firstSession, second.SessionId);

            var logs = await second.InvokeAsync<JObject>("GetLogs");
            Assert.AreEqual(0, logs["Entries"].Count(),
                "a fresh lease must start with an empty audit trail even on a reused process");
        }

        /// <summary>
        /// ResetAsync must WAIT for the adapter to confirm, not merely queue the frame.
        ///
        /// It used to return a completed task the moment the frame was written, and AdapterPool
        /// treated that as proof the session boundary had taken effect before putting the instance
        /// back in the pool — so a later lease could be handed a process still carrying the
        /// previous session's state.
        ///
        /// Asserted on elapsed time against an adapter whose reset deliberately takes 400ms,
        /// because that is the property that actually changed. A pool-level test does not
        /// discriminate: the adapter dispatches Reset before the next command anyway, so the old
        /// code passed on ordering luck.
        /// </summary>
        [TestMethod]
        public async Task Reset_waits_for_the_adapter_to_confirm_it()
        {
            var spec = Spec("slow-reset");
            spec.StartupValues["ResetDelayMs"] = "400";

            var instance = await adapters.StartExclusiveAsync(spec);

            var clock = Stopwatch.StartNew();
            await instance.ResetAsync("session-1");
            clock.Stop();

            Assert.IsTrue(clock.ElapsedMilliseconds >= 350,
                $"ResetAsync returned in {clock.ElapsedMilliseconds}ms against a 400ms reset, so it " +
                "did not wait for the adapter — the pool would hand this instance to the next " +
                "session before the boundary had been applied");
        }

        [TestMethod]
        public async Task Leases_reuse_a_warm_process_rather_than_spawning_one_per_call()
        {
            var pids = new HashSet<int>();

            for (var i = 0; i < 4; i++)
            {
                await using var lease = await adapters.RentAsync(Spec());
                await lease.InvokeAsync<JObject>("TestConnection");
                pids.Add(lease.Instance.Process.Id);
            }

            Assert.IsTrue(pids.Count <= 2,
                $"four sequential leases used {pids.Count} processes; a pool of 2 should reuse them");
        }

        // ------------------------------------------------------------------ upstream

        class FakeCarrier : Carrier.CarrierBase
        {
            public static int FailNextCalls;

            public override Task<PingReply> Ping(PingRequest request, ServerCallContext context) =>
                Task.FromResult(new PingReply { Version = "fake-carrier/1.0" });

            public override Task<CreateShipmentReply> CreateShipment(
                CreateShipmentRequest request, ServerCallContext context)
            {
                if (FailNextCalls > 0)
                {
                    FailNextCalls--;
                    throw new RpcException(new Status(StatusCode.Unavailable, "try again"));
                }

                if (request.Parcels.Any(p => p.WeightKg > 31.5))
                    return Task.FromResult(new CreateShipmentReply
                    {
                        Accepted = false,
                        ErrorCode = "PARCEL_TOO_HEAVY",
                        ErrorMessage = "over the limit"
                    });

                return Task.FromResult(new CreateShipmentReply
                {
                    Accepted = true,
                    TrackingNumber = $"FK{Random.Shared.NextInt64(10000000, 99999999)}",
                    Price = 5.25,
                    Currency = "EUR"
                });
            }
        }
    }
}
