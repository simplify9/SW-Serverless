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
                await packager.PublishAsync(ClassicId, "SW.Serverless.UnitTests.Adapter",
                    new Dictionary<string, string> { ["Lifecycle"] = "invocation" });
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

                logger.LogInformation("Sample adapters installed from storage and running.");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                logger.LogError(ex, "Could not start the resident adapters.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
