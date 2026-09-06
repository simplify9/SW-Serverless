using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.FolderSource
{
    /// <summary>
    /// The reference shape for a DATA SOURCE adapter, using a folder instead of a broker so it
    /// runs with no infrastructure. A RabbitMQ or Kafka adapter is this same class with a
    /// different client: a consume loop that pushes, a publish command, topology, discovery and
    /// a real status.
    ///
    /// The ordering below is the part worth copying: the file is only archived AFTER the host
    /// acknowledges. Crash in between and the file is still there, so it is redelivered — which
    /// is exactly why the dedupe key is mandatory (design doc, section 5).
    /// </summary>
    public class Handler : IResidentAdapter
    {
        IAdapterContext context;
        CancellationTokenSource cts;
        Task loop;

        string root;
        string archive;
        string pattern = "*.json";
        int pollSeconds = 2;

        long received, acked, nacked, failed;
        DateTimeOffset? lastMessageOn;
        string lastError;
        volatile string state = "Starting";

        // ------------------------------------------------------------------ lifecycle

        public Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
        {
            this.context = context;

            root = context.StartupValueOf("Path")
                   ?? throw new InvalidOperationException("Startup value 'Path' is required.");
            archive = context.StartupValueOf("ArchivePath") ?? Path.Combine(root, ".archive");
            pattern = context.StartupValueOf("Pattern") ?? pattern;
            if (int.TryParse(context.StartupValueOf("PollSeconds"), out var p) && p > 0) pollSeconds = p;

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(archive);

            state = "Connected";
            context.LogInformation($"Watching {root} for {pattern} every {pollSeconds}s.");

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loop = Task.Run(() => ConsumeAsync(cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            state = "Draining";
            cts?.Cancel();
            if (loop != null) await Task.WhenAny(loop, Task.Delay(5000, cancellationToken));
            state = "Stopped";
            context?.LogInformation("Folder source stopped.");
        }

        public Task<AdapterStatus> GetStatusAsync()
        {
            var pending = 0;
            try { pending = Directory.Exists(root) ? Directory.GetFiles(root, pattern).Length : 0; }
            catch { /* status must never throw */ }

            var status = new AdapterStatus
            {
                Connected = Directory.Exists(root),
                // Idle and Disconnected are genuinely different things, and a plain liveness
                // probe cannot tell them apart (design doc, section 6.4).
                State = !Directory.Exists(root) ? "Disconnected"
                      : pending == 0 && received == 0 ? "Idle"
                      : state,
                LastMessageOn = lastMessageOn,
                LastError = lastError
            };
            status.Details["root"] = root;
            status.Details["pending"] = pending.ToString();
            status.Details["received"] = received.ToString();
            status.Details["acked"] = acked.ToString();
            status.Details["nacked"] = nacked.ToString();
            status.Details["failed"] = failed.ToString();
            return Task.FromResult(status);
        }

        // ------------------------------------------------------------------ ingress

        async Task ConsumeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, pattern).OrderBy(f => f))
                    {
                        if (ct.IsCancellationRequested) return;
                        await DeliverAsync(file, ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    context.LogError("Consume loop failed.", ex);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        async Task DeliverAsync(string file, CancellationToken ct)
        {
            byte[] payload;
            try { payload = await File.ReadAllBytesAsync(file, ct); }
            catch (IOException) { return; }   // still being written; next poll will get it

            received++;

            var headers = new Dictionary<string, string>
            {
                ["file.name"] = Path.GetFileName(file),
                ["file.size"] = payload.Length.ToString()
            };

            var result = await context.PublishAsync(
                payload,
                // Content-addressed, so a redelivery after a crash is recognisable as the same
                // message even if the file were renamed.
                dedupeKey: $"folder:{Convert.ToHexString(SHA256.HashData(payload))[..32]}",
                endpoint: Path.GetFileName(root),
                headers: headers,
                contentType: "application/json",
                cancellationToken: ct);

            if (result.Accepted)
            {
                // ONLY NOW is it safe to "commit the offset".
                acked++;
                lastMessageOn = DateTimeOffset.UtcNow;
                var target = Path.Combine(archive, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Path.GetFileName(file)}");
                try { File.Move(file, target, overwrite: true); }
                catch (Exception ex) { context.LogWarning($"Archiving {file} failed; it will be redelivered.", ex); }

                context.Metric("foldersource.acked", 1);
            }
            else
            {
                // Left in place deliberately: the host said no, so this is a redelivery, not a loss.
                nacked++;
                lastError = result.Error;
                context.LogWarning($"Host rejected {Path.GetFileName(file)}: {result.Error}. Leaving it for redelivery.");
                context.Metric("foldersource.nacked", 1);
            }
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Egress. A broker adapter publishes; this one writes a file.</summary>
        public async Task<object> Publish(PublishRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Endpoint))
                throw new ArgumentException("Endpoint is required.");

            var folder = Path.Combine(root, request.Endpoint);
            Directory.CreateDirectory(folder);

            var name = request.Key ?? $"{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
            var path = Path.Combine(folder, name);
            await File.WriteAllTextAsync(path, request.Body ?? "");

            context.LogInformation($"Published to {path}.");
            return new { path, bytes = Encoding.UTF8.GetByteCount(request.Body ?? "") };
        }

        /// <summary>The "test connection" control the UI needs before anything is saved.</summary>
        public Task<object> TestConnection()
        {
            var checks = new List<object>();
            var exists = Directory.Exists(root);
            checks.Add(new { step = "reachable", ok = exists, detail = root });

            var writable = false;
            if (exists)
            {
                try
                {
                    var probe = Path.Combine(root, $".probe-{Guid.NewGuid():N}");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    writable = true;
                }
                catch { }
            }
            checks.Add(new { step = "writable", ok = writable, detail = writable ? "" : "no write permission" });

            return Task.FromResult<object>(new { ok = exists && writable, checks });
        }

        /// <summary>The "discover the cluster" experience: what already exists over there.</summary>
        public Task<object> Discover()
        {
            var endpoints = Directory.Exists(root)
                ? Directory.GetDirectories(root)
                    .Where(d => !Path.GetFileName(d).StartsWith("."))
                    .Select(d => new
                    {
                        name = Path.GetFileName(d),
                        messages = Directory.GetFiles(d).Length
                    })
                    .ToArray()
                : Array.Empty<object>();

            return Task.FromResult<object>(new { root, endpoints });
        }

        /// <summary>Topology provisioning, in the declare-if-absent shape a broker would use.</summary>
        public Task<object> DeclareTopology(TopologyRequest request)
        {
            var created = new List<string>();
            foreach (var endpoint in request?.Endpoints ?? new List<string>())
            {
                var path = Path.Combine(root, endpoint);
                if (Directory.Exists(path)) continue;
                if (request.Mode == "assert")
                    throw new InvalidOperationException($"Endpoint '{endpoint}' does not exist and mode is 'assert'.");
                Directory.CreateDirectory(path);
                created.Add(endpoint);
            }
            return Task.FromResult<object>(new { created });
        }

        public class PublishRequest
        {
            public string Endpoint { get; set; }
            public string Key { get; set; }
            public string Body { get; set; }
        }

        public class TopologyRequest
        {
            /// <summary>none, assert or create — mirrors the declareMode in the provider plan.</summary>
            public string Mode { get; set; } = "create";
            public List<string> Endpoints { get; set; } = new();
        }
    }
}
