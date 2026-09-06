using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SW.Serverless.Resident;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.SampleWeb.Services
{
    /// <summary>
    /// Packages the sample adapters into the local store on startup, then starts the resident
    /// ones BY ADAPTER ID — so the runtime downloads, extracts and launches them, which is the
    /// "install a provider without redeploying" claim actually being exercised.
    /// </summary>
    public class DemoBootstrapper : IHostedService
    {
        public static readonly string InboxPath =
            Path.Combine(Path.GetTempPath(), "swsl-sampleweb", "inbox");

        public const string TickerId = "sample.ticker";
        public const string FolderSourceId = "sample.foldersource";
        public const string ClassicId = "sample.classic";
        public const string RabbitPublisherId = "rabbit.publisher";
        public const string RabbitConsumerId = "rabbit.consumer";
        public const string LargeFilesId = "sample.largefiles";
        public const string CarrierId = "sample.carrier";

        public static readonly string DropPath =
            Path.Combine(Path.GetTempPath(), "swsl-sampleweb", "drop");

        const string Exchange = "swsl.sample";
        const string RoutingKey = "sample.tick";

        /// <summary>Set false, or RabbitMq:Host in configuration, to point at another broker.</summary>
        public static string BrokerHost = Environment.GetEnvironmentVariable("SWSL_RABBIT_HOST") ?? "localhost";

        public bool RabbitAvailable { get; private set; }
        public string RabbitError { get; private set; }

        readonly AdapterPackager packager;
        readonly IResidentAdapterHost adapters;
        readonly ILogger<DemoBootstrapper> logger;

        public DemoBootstrapper(AdapterPackager packager, IResidentAdapterHost adapters,
            ILogger<DemoBootstrapper> logger)
        {
            this.packager = packager;
            this.adapters = adapters;
            this.logger = logger;
        }

        public string LastError { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(InboxPath);
            Directory.CreateDirectory(DropPath);

            try
            {
                await packager.PublishAsync(TickerId, "SW.Serverless.Samples.Ticker",
                    new Dictionary<string, string>
                    {
                        ["Protocol"] = "2", ["Lifecycle"] = "resident", ["MaxInFlight"] = "8"
                    });

                await packager.PublishAsync(FolderSourceId, "SW.Serverless.Samples.FolderSource",
                    new Dictionary<string, string>
                    {
                        ["Protocol"] = "2", ["Lifecycle"] = "resident", ["MaxInFlight"] = "8"
                    });

                // No Protocol key: the classic per-invocation path, unchanged.
                await packager.PublishAsync(ClassicId, "SW.Serverless.Samples.Classic",
                    new Dictionary<string, string> { ["Lifecycle"] = "invocation" });

                await packager.PublishAsync(RabbitPublisherId, "SW.Serverless.Samples.RabbitPublisher",
                    new Dictionary<string, string>
                    {
                        ["Protocol"] = "2", ["Lifecycle"] = "resident", ["MaxInFlight"] = "8"
                    });

                await packager.PublishAsync(LargeFilesId, "SW.Serverless.Samples.LargeFiles",
                    new Dictionary<string, string>
                    {
                        // A small credit window on purpose: chunks are large, so the host holding
                        // only four unacked at a time is what keeps memory flat under load.
                        ["Protocol"] = "2", ["Lifecycle"] = "resident", ["MaxInFlight"] = "4"
                    });

                await packager.PublishAsync(CarrierId, "SW.Serverless.Samples.Carrier",
                    new Dictionary<string, string>
                    {
                        ["Protocol"] = "2", ["Lifecycle"] = "resident",
                        // Poolable: the handler implements IResettable, so a session boundary
                        // exists and warm instances can safely be checked out per call.
                        ["Poolable"] = "true", ["PoolSize"] = "3"
                    });

                await packager.PublishAsync(RabbitConsumerId, "SW.Serverless.Samples.RabbitConsumer",
                    new Dictionary<string, string>
                    {
                        ["Protocol"] = "2", ["Lifecycle"] = "resident", ["MaxInFlight"] = "32"
                    });
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                logger.LogError(ex, "Could not package the sample adapters. Build the solution first.");
                return;
            }

            try
            {
                // Note there is no path here — only an id. Everything else is resolved from storage.
                await adapters.StartExclusiveAsync(new AdapterSpec
                {
                    AdapterId = TickerId,
                    InstanceKey = "ds-ticker",
                    StartupValues = { ["IntervalSeconds"] = "4" }
                }, cancellationToken);

                await adapters.StartExclusiveAsync(new AdapterSpec
                {
                    AdapterId = FolderSourceId,
                    InstanceKey = "ds-inbox",
                    StartupValues =
                    {
                        ["Path"] = InboxPath,
                        ["Pattern"] = "*.json",
                        ["PollSeconds"] = "2"
                    }
                }, cancellationToken);

                await adapters.StartExclusiveAsync(new AdapterSpec
                {
                    AdapterId = LargeFilesId,
                    InstanceKey = "ds-largefiles",
                    StartupValues =
                    {
                        ["Path"] = DropPath,
                        ["Pattern"] = "*.bin",
                        ["ChunkSizeKb"] = "256",
                        ["PollSeconds"] = "2",
                        // Slow enough that progress is watchable rather than instantaneous.
                        ["ThrottleMsPerChunk"] = "8"
                    }
                }, cancellationToken);

                await adapters.StartExclusiveAsync(new AdapterSpec
                {
                    AdapterId = CarrierId,
                    InstanceKey = "ds-carrier",
                    StartupValues =
                    {
                        ["BaseUrl"] = "http://localhost:5200",
                        ["Account"] = "SW-ACCT-9931",
                        ["ApiKey"] = "sample-key-not-a-real-secret",
                        ["DefaultService"] = "EXPRESS",
                        ["MaxAttempts"] = "3",
                        ["TimeoutSeconds"] = "15"
                    }
                }, cancellationToken);

                logger.LogInformation("Sample adapters installed from storage and running.");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                logger.LogError(ex, "Could not start the resident adapters.");
            }

            // The broker pair is optional: with no RabbitMQ reachable the two adapters simply do
            // not start, and the rest of the dashboard is unaffected. That is the intended
            // failure mode — one data source being down is not a node-wide outage.
            try
            {
                // Consumer first, so the queue and binding exist before anything is published.
                await adapters.StartExclusiveAsync(new AdapterSpec
                {
                    AdapterId = RabbitConsumerId,
                    InstanceKey = "ds-rabbit-in",
                    StartupValues =
                    {
                        ["Host"] = BrokerHost,
                        ["Exchange"] = Exchange,
                        ["ExchangeType"] = "topic",
                        ["RoutingKey"] = RoutingKey,
                        ["Queue"] = "swsl.sample.inbound",
                        ["Prefetch"] = "32",
                        ["AutoDelete"] = "true"
                    }
                }, cancellationToken);

                await adapters.StartExclusiveAsync(new AdapterSpec
                {
                    AdapterId = RabbitPublisherId,
                    InstanceKey = "ds-rabbit-out",
                    StartupValues =
                    {
                        ["Host"] = BrokerHost,
                        ["Exchange"] = Exchange,
                        ["ExchangeType"] = "topic",
                        ["RoutingKey"] = RoutingKey,
                        ["IntervalMs"] = "10",
                        ["PublisherConfirms"] = "true"
                    }
                }, cancellationToken);

                RabbitAvailable = true;
                logger.LogInformation("RabbitMQ pair running against {Host}: publishing every 10 ms.", BrokerHost);
            }
            catch (Exception ex)
            {
                RabbitError = ex.Message;
                logger.LogWarning("RabbitMQ samples not started ({Reason}). " +
                    "Start a broker with: docker run -d --rm -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management",
                    ex.Message);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
