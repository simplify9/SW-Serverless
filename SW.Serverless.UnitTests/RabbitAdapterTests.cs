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
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.RabbitMq;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// End-to-end coverage of the two RabbitMQ sample adapters against a real broker.
    ///
    /// These need Docker. When it is not available every test reports Inconclusive rather than
    /// failing, so the suite still runs on a machine or a CI job without a container runtime.
    /// </summary>
    [TestClass]
    public class RabbitAdapterTests
    {
        const string PublisherId = "test.rabbit.publisher";
        const string ConsumerId = "test.rabbit.consumer";
        const string RoutingKey = "tests.tick";

        // Every test gets its OWN exchange. Sharing one meant each publisher fed every other
        // test's consumer, which is a real isolation bug and not an adapter one.
        static string ExchangeFor(string key) => $"swsl.tests.{key}";

        static RabbitMqContainer broker;
        static IHost host;
        static IResidentAdapterHost adapters;
        static TestEventSink sink;
        static string skipReason;

        static string brokerHost;
        static int brokerPort;
        static string brokerUser = "guest";
        static string brokerPassword = "guest";

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            // Deliberate opt-out for a CI job that has no container runtime, or does not want to
            // pay for one. Without it the tests still degrade to Inconclusive on their own.
            if (Environment.GetEnvironmentVariable("SWSL_SKIP_BROKER_TESTS") == "1")
            {
                skipReason = "SWSL_SKIP_BROKER_TESTS=1 — broker-backed tests were skipped on purpose.";
                return;
            }

            try
            {
                broker = new RabbitMqBuilder()
                    .WithImage("rabbitmq:3.13-management")
                    .Build();

                using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await broker.StartAsync(startup.Token);

                var uri = new Uri(broker.GetConnectionString());
                brokerHost = uri.Host;
                brokerPort = uri.Port;
                if (uri.UserInfo.Split(':') is [var user, var password])
                {
                    brokerUser = user;
                    brokerPassword = password;
                }
            }
            catch (Exception ex)
            {
                skipReason = $"Docker is not available, so the RabbitMQ tests cannot run: {ex.Message}";
                return;
            }

            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-rabbit");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-rabbittests", "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-r{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-r{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(45);
                        o.MaxInFlight = 16;
                    });
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                })
                .Build();

            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();
            sink = host.Services.GetRequiredService<TestEventSink>();

            var cloudFiles = host.Services.GetRequiredService<ICloudFilesService>();
            var metadata = new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" };
            await TestStore.PublishAsync(cloudFiles, PublisherId, "SW.Serverless.Samples.RabbitPublisher", metadata);
            await TestStore.PublishAsync(cloudFiles, ConsumerId, "SW.Serverless.Samples.RabbitConsumer", metadata);
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) { await host.StopAsync(); host.Dispose(); }
            if (broker != null) await broker.DisposeAsync();
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "swsl-rabbittests"), true); } catch { }
        }

        [TestInitialize]
        public void TestInitialize()
        {
            if (skipReason != null) Assert.Inconclusive(skipReason);
            sink.Clear();
            sink.RejectEverything = false;
        }

        /// <summary>
        /// Stop every adapter this test started. Without it each test leaves two live processes
        /// behind that keep publishing into the shared sink, so a later test sees the previous
        /// one's messages — and the run ends up holding two dozen orphaned children.
        /// </summary>
        [TestCleanup]
        public async Task TestCleanup()
        {
            if (skipReason != null || adapters == null) return;

            foreach (var health in adapters.Describe())
                await adapters.StopAsync(health.AdapterId, health.InstanceKey, drain: false);
        }

        // ------------------------------------------------------------------ publisher

        [TestMethod]
        public async Task Publisher_publishes_and_the_broker_confirms()
        {
            var publisher = await StartPublisher("pub-confirm", intervalMs: 20);

            await WaitFor(async () => (await Stats(publisher)).Value<long>("confirmed") > 5,
                TimeSpan.FromSeconds(20), "the broker never confirmed a publish");

            var stats = await Stats(publisher);

            Assert.IsTrue(stats.Value<bool>("confirmsEnabled"));
            Assert.IsTrue(stats.Value<long>("published") > 0);
            Assert.AreEqual(0, stats.Value<long>("failed"));
        }

        /// <summary>
        /// mandatory:true plus a BasicReturn handler is how a publisher learns a message was
        /// unroutable. Without it the broker discards it and nobody ever finds out.
        /// </summary>
        [TestMethod]
        public async Task An_unroutable_publish_comes_back_as_a_return()
        {
            var publisher = await StartPublisher("pub-unroutable");

            // Measure a DELTA, not an absolute. Nothing is bound to this test's exchange, so the
            // publisher's own first tick is unroutable too and races with the one below —
            // asserting "returned == 1" made this test flaky under load.
            var before = (await Stats(publisher)).Value<long>("returned");

            await publisher.InvokeAsync<object>("PublishUnroutable");

            await WaitFor(async () => (await Stats(publisher)).Value<long>("returned") > before,
                TimeSpan.FromSeconds(30), "the broker never returned the unroutable message");
        }

        [TestMethod]
        public async Task Publish_interval_changes_at_runtime()
        {
            var publisher = await StartPublisher("pub-interval");

            var result = await publisher.InvokeAsync<JObject>("SetInterval", 25);
            Assert.AreEqual(25, result.Value<int>("intervalMs"));

            var restarts = adapters.Describe().Single(h => h.InstanceKey == "pub-interval").RestartCount;
            Assert.AreEqual(0, restarts, "changing the rate must not restart the adapter");

            Assert.AreEqual(25, (await Stats(publisher)).Value<int>("intervalMs"));
        }

        // ------------------------------------------------------------------ consumer

        [TestMethod]
        public async Task Consumer_receives_what_the_publisher_sends()
        {
            var queue = await StartPair("flow", intervalMs: 20);

            await WaitFor(() => sink.Delivered.Any(d => d.AdapterId == ConsumerId),
                TimeSpan.FromSeconds(25), "nothing reached the host through the broker");

            var delivered = sink.Delivered.First(d => d.AdapterId == ConsumerId);
            Assert.AreEqual(queue, delivered.Endpoint);
            Assert.IsTrue(delivered.Accepted);
            StringAssert.Contains(delivered.Body, "sequence");
        }

        /// <summary>
        /// The dedupe key must come from the broker's own message id. A delivery tag would be
        /// wrong: tags are per channel and restart at 1 on every reconnect, so two unrelated
        /// messages would collide.
        /// </summary>
        [TestMethod]
        public async Task Dedupe_key_comes_from_the_broker_message_id()
        {
            await StartPair("dedupe", intervalMs: 20);

            await WaitFor(() => sink.Delivered.Any(d => d.AdapterId == ConsumerId),
                TimeSpan.FromSeconds(25), "nothing was delivered");

            var key = sink.Delivered.First(d => d.AdapterId == ConsumerId).DedupeKey;

            StringAssert.StartsWith(key, $"rabbit:{ExchangeFor("dedupe")}:");
            StringAssert.Contains(key, "dedupe-out-",
                "the key should carry the publisher's message id, not a delivery tag");
        }

        /// <summary>
        /// Ack ordering, against a real broker. A host rejection must become BasicNack(requeue)
        /// so the message returns to the queue — and the redelivery must arrive flagged, with the
        /// same dedupe key, so the host can recognise it instead of persisting it twice.
        /// </summary>
        [TestMethod]
        public async Task A_rejected_message_is_nacked_back_and_redelivered()
        {
            sink.RejectEverything = true;

            var queue = await StartPair("reject", intervalMs: 60000);   // no auto-publishing
            var publisher = adapters.Get(PublisherId, "reject-out");

            await publisher.InvokeAsync<object>("PublishOne", "{\"only\":\"one\"}");

            await WaitFor(() => sink.Delivered.Any(d => !d.Accepted),
                TimeSpan.FromSeconds(20), "the rejected delivery never arrived");

            var rejectedKey = sink.Delivered.First(d => !d.Accepted).DedupeKey;

            // It went back on the queue rather than being consumed away.
            var consumer = adapters.Get(ConsumerId, "reject-in");
            await WaitFor(async () => (await Stats(consumer)).Value<long>("nacked") > 0,
                TimeSpan.FromSeconds(15), "the consumer never nacked it");

            // Now accept, and the redelivery should be acked.
            sink.RejectEverything = false;

            await WaitFor(() => sink.Delivered.Any(d => d.Accepted && d.DedupeKey == rejectedKey),
                TimeSpan.FromSeconds(25), "the message was never redelivered after acceptance");

            await WaitFor(async () => (await Stats(consumer)).Value<long>("acked") > 0,
                TimeSpan.FromSeconds(15), "the consumer never acked the redelivery");

            var stats = await Stats(consumer);
            Assert.AreEqual(0, stats.Value<long>("failed"));
            Assert.AreEqual(queue, stats.Value<string>("queueName"));
        }

        [TestMethod]
        public async Task Prefetch_changes_at_runtime()
        {
            var consumer = await StartConsumer("prefetch", "swsl.tests.prefetch");

            var result = await consumer.InvokeAsync<JObject>("SetPrefetch", 4);
            Assert.AreEqual(4, result.Value<int>("prefetch"));
            Assert.AreEqual(4, (await Stats(consumer)).Value<int>("prefetch"));
        }

        [TestMethod]
        public async Task Purge_empties_the_queue()
        {
            var exchange = ExchangeFor("purge");
            var queue = "q.purge";

            // Consumer first so the queue and binding exist, then stop it so nothing drains.
            await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = ConsumerId, InstanceKey = "purge-in",
                StartupValues = Common(exchange, queue)
            });
            await adapters.StopAsync(ConsumerId, "purge-in", drain: false);

            var publisher = await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = PublisherId, InstanceKey = "purge-out",
                StartupValues = Common(exchange, intervalMs: 60000)
            });
            for (var i = 0; i < 5; i++)
                await publisher.InvokeAsync<object>("PublishOne", $"{{\"n\":{i}}}");

            // Re-attach only to purge and read depth.
            var consumer = await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = ConsumerId, InstanceKey = "purge-check",
                StartupValues = Common(exchange, queue)
            });
            var purged = await consumer.InvokeAsync<JObject>("PurgeQueue");

            Assert.IsTrue(purged.Value<int>("purged") >= 0);
            await WaitFor(async () => (await Stats(consumer)).Value<int?>("depth") == 0,
                TimeSpan.FromSeconds(15), "the queue did not end up empty");
        }

        // ------------------------------------------------------------------ controls

        [TestMethod]
        public async Task Test_connection_reports_each_stage()
        {
            var publisher = await StartPublisher("probe");

            var result = await publisher.InvokeAsync<JObject>("TestConnection");

            Assert.IsTrue(result.Value<bool>("ok"));
            var steps = result["steps"].Select(s => s.Value<string>("step")).ToArray();
            CollectionAssert.AreEquivalent(new[] { "connect", "channel", "exchange" }, steps);
        }

        [TestMethod]
        public async Task Discover_returns_the_topology_that_is_actually_there()
        {
            var queue = "swsl.tests.discover";
            var consumer = await StartConsumer("discover", queue);

            var result = await consumer.InvokeAsync<JObject>("Discover");

            Assert.AreEqual(ExchangeFor("discover"), result["exchange"].Value<string>("name"));
            Assert.AreEqual("topic", result["exchange"].Value<string>("type"));
            Assert.AreEqual(queue, result["queue"].Value<string>("name"));
            Assert.AreEqual(1, result["queue"].Value<int>("consumers"), "this adapter is the consumer");
            Assert.AreEqual(RoutingKey, result["binding"].Value<string>("routingKey"));
        }

        [TestMethod]
        public async Task The_adapter_advertises_its_commands_on_attach()
        {
            await StartConsumer("advertise", "swsl.tests.advertise");

            var health = adapters.Describe().Single(h => h.InstanceKey == "advertise");

            CollectionAssert.IsSubsetOf(
                new[] { "Discover", "SetPrefetch", "PurgeQueue", "TestConnection", "DeclareTopology" },
                health.Commands.ToArray());
            CollectionAssert.Contains(health.Capabilities.ToArray(), "resident");
            Assert.AreEqual(2, health.ProtocolVersion);
        }

        [TestMethod]
        public async Task Health_carries_the_broker_detail_from_the_heartbeat()
        {
            await StartConsumer("health", "swsl.tests.health");

            await WaitFor(() => adapters.Describe()
                    .Single(h => h.InstanceKey == "health").Details.ContainsKey("queue"),
                TimeSpan.FromSeconds(20), "no heartbeat detail arrived");

            var health = adapters.Describe().Single(h => h.InstanceKey == "health");

            Assert.IsTrue(health.Connected);
            Assert.AreEqual("swsl.tests.health", health.Details["queue"]);
            Assert.AreEqual(ExchangeFor("health"), health.Details["exchange"]);
            Assert.IsTrue(health.WorkingSetBytes > 0, "host-observed memory is sampled independently");
        }

        // ------------------------------------------------------------------ helpers

        static Dictionary<string, string> Common(string exchange, string queue = null, int intervalMs = 0)
        {
            var values = new Dictionary<string, string>
            {
                ["Host"] = brokerHost,
                ["Port"] = brokerPort.ToString(),
                ["UserName"] = brokerUser,
                ["Password"] = brokerPassword,
                ["Exchange"] = exchange,
                ["ExchangeType"] = "topic",
                ["RoutingKey"] = RoutingKey
            };
            if (queue != null) values["Queue"] = queue;
            if (intervalMs > 0) values["IntervalMs"] = intervalMs.ToString();
            return values;
        }

        // Default to a very slow interval so tests are driven by explicit PublishOne calls
        // rather than by a background flood.
        static Task<ResidentAdapterInstance> StartPublisher(string key, int intervalMs = 60000) =>
            adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = PublisherId,
                InstanceKey = key,
                StartupValues = Common(ExchangeFor(key), intervalMs: intervalMs)
            });

        static Task<ResidentAdapterInstance> StartConsumer(string key, string queue) =>
            adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = ConsumerId,
                InstanceKey = key,
                StartupValues = Common(ExchangeFor(key), queue)
            });

        /// <summary>Consumer first, so the queue and binding exist before anything is published.</summary>
        static async Task<string> StartPair(string key, int intervalMs)
        {
            var exchange = ExchangeFor(key);
            var queue = $"q.{key}";

            await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = ConsumerId,
                InstanceKey = $"{key}-in",
                StartupValues = Common(exchange, queue)
            });
            await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = PublisherId,
                InstanceKey = $"{key}-out",
                StartupValues = Common(exchange, intervalMs: intervalMs)
            });
            return queue;
        }

        static Task<JObject> Stats(ResidentAdapterInstance instance) =>
            instance.InvokeAsync<JObject>("GetStats", timeoutSeconds: 15);

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
                try { if (await condition()) return; } catch { /* adapter may still be settling */ }
                await Task.Delay(300);
            }
            Assert.Fail($"Timed out after {timeout}: {because}");
        }
    }
}
