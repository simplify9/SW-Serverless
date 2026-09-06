using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SW.Serverless;
using SW.Serverless.Resident;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Host
{
    static class Program
    {
        static readonly string Demo = Path.Combine(Path.GetTempPath(), "swsl-demo");

        static async Task Main()
        {
            Directory.CreateDirectory(Path.Combine(Demo, "inbox"));

            using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureLogging(l =>
                {
                    l.ClearProviders();
                    l.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
                    l.SetMinimumLevel(LogLevel.Information);
                    l.AddFilter("Microsoft", LogLevel.Warning);
                    l.AddFilter("Grpc", LogLevel.Warning);
                })
                .ConfigureServices(s => s.AddResidentAdapters<ConsoleEventSink>(o =>
                {
                    o.HeartbeatInterval = TimeSpan.FromSeconds(10);
                    o.MaxInFlight = 8;
                    o.SoftMemoryLimitBytes = 512L * 1024 * 1024;
                }))
                .Build();

            await host.StartAsync();

            var adapters = host.Services.GetRequiredService<IResidentAdapterHost>();
            var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("demo");

            log.LogInformation("Demo folder: {Demo}", Demo);

            var ticker = await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = "sample.ticker",
                InstanceKey = "ds-1",
                EntryAssemblyPath = Locate("SW.Serverless.Samples.Ticker"),
                StartupValues = { ["IntervalSeconds"] = "3" },
                AdapterValues = { ["Lifecycle"] = "resident", ["Protocol"] = "2" }
            });

            var folder = await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = "sample.foldersource",
                InstanceKey = "ds-2",
                EntryAssemblyPath = Locate("SW.Serverless.Samples.FolderSource"),
                StartupValues =
                {
                    ["Path"] = Path.Combine(Demo, "inbox"),
                    ["Pattern"] = "*.json",
                    ["PollSeconds"] = "2"
                },
                AdapterValues = { ["Lifecycle"] = "resident", ["Protocol"] = "2" }
            });

            log.LogInformation("Both adapters attached and running.");

            await Task.Delay(1500);
            await DemonstrateAsync(ticker, folder, log);

            _ = Task.Run(() => StatusLoopAsync(adapters, log, host.Services
                .GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));

            log.LogInformation("Running. Drop a *.json file into {Inbox} to see ingress. Ctrl+C to stop.",
                Path.Combine(Demo, "inbox"));

            await host.WaitForShutdownAsync();
        }

        static async Task DemonstrateAsync(ResidentAdapterInstance ticker,
            ResidentAdapterInstance folder, ILogger log)
        {
            // 1. Commands still work by method name, exactly as in the classic runner.
            var counters = await ticker.InvokeAsync<Dictionary<string, object>>("GetCounters");
            log.LogInformation("CMD  ticker.GetCounters -> {Counters}",
                string.Join(", ", counters.Select(kv => $"{kv.Key}={kv.Value}")));

            // 2. Runtime reconfiguration, no restart.
            await ticker.InvokeAsync<object>("SetInterval", 5);
            log.LogInformation("CMD  ticker.SetInterval(5) -> interval changed while running");

            // 3. A failing command returns a typed error rather than hanging or corrupting
            //    the next caller's result, which is the v1 defect this protocol fixes.
            try
            {
                await ticker.InvokeAsync<object>("Explode");
            }
            catch (AdapterInvocationException ex)
            {
                log.LogInformation("CMD  ticker.Explode -> {Type}: {Message}", ex.AdapterExceptionType, ex.Message);
            }

            // 4. The controls a data-source UI needs.
            log.LogInformation("CMD  folder.TestConnection -> {Result}",
                await folder.InvokeAsync<string>("TestConnection"));

            await folder.InvokeAsync<object>("DeclareTopology",
                new { Mode = "create", Endpoints = new[] { "orders", "invoices" } });
            log.LogInformation("CMD  folder.DeclareTopology(orders, invoices) -> created");

            log.LogInformation("CMD  folder.Discover -> {Result}",
                await folder.InvokeAsync<string>("Discover"));

            // 5. Egress — the host pushing outward through the adapter.
            log.LogInformation("CMD  folder.Publish -> {Result}", await folder.InvokeAsync<string>("Publish",
                new { Endpoint = "orders", Key = "demo-order.json", Body = "{\"orderId\":1001}" }));

            // 6. Ingress — dropping a file makes the adapter push it back in.
            await File.WriteAllTextAsync(
                Path.Combine(Demo, "inbox", $"sample-{DateTime.UtcNow:HHmmss}.json"),
                "{\"hello\":\"from the inbox\"}");
            log.LogInformation("Dropped a file into the inbox; expect an EVENT line within a poll.");
        }

        static async Task StatusLoopAsync(IResidentAdapterHost adapters, ILogger log, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                catch (OperationCanceledException) { return; }

                foreach (var instance in adapters.List())
                {
                    var status = instance.LastStatus;
                    if (status == null) continue;

                    var rss = 0L;
                    try { instance.Process?.Refresh(); rss = instance.Process?.WorkingSet64 ?? 0; } catch { }

                    log.LogInformation(
                        "STATUS     {Adapter}/{Key}  state={State} connected={Connected} rss={Rss}MB restarts={Restarts}  {Details}",
                        instance.AdapterId, instance.InstanceKey, status.State, status.Connected,
                        rss / 1024 / 1024, instance.RestartCount,
                        string.Join(" ", status.Details.Select(kv => $"{kv.Key}={kv.Value}")));
                }
            }
        }

        /// <summary>
        /// A sample shortcut. In the real host this is the existing S3 download-and-extract step,
        /// which is unchanged by protocol 2 — see the design doc, section 15.2.
        /// </summary>
        static string Locate(string project)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, project))) dir = dir.Parent;
            if (dir == null) throw new DirectoryNotFoundException($"Could not locate the {project} project.");

            var configuration = Path.Combine(dir.FullName, project, "bin",
#if DEBUG
                "Debug",
#else
                "Release",
#endif
                "net8.0", project + ".dll");

            if (!File.Exists(configuration))
                throw new FileNotFoundException($"Build {project} first.", configuration);

            return configuration;
        }
    }
}
