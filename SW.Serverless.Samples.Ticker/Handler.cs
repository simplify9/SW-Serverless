using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Ticker
{
    /// <summary>
    /// The smallest possible resident adapter: it stays running and pushes an event on a timer.
    /// Nothing external is required, so it is the right thing to run first when proving the
    /// transport, the heartbeat and the push/ack handshake.
    /// </summary>
    public class Handler : IResidentAdapter
    {
        IAdapterContext context;
        Task loop;
        CancellationTokenSource cts;

        int intervalSeconds = 3;
        long produced, accepted, rejected;
        DateTimeOffset? lastMessageOn;
        string lastError;

        // ------------------------------------------------------------------ lifecycle

        public Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
        {
            this.context = context;

            // Configuration arrives over the stream, never on argv.
            if (int.TryParse(context.StartupValueOf("IntervalSeconds"), out var configured) && configured > 0)
                intervalSeconds = configured;

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            context.LogInformation($"Ticker starting, every {intervalSeconds}s.",
                new Dictionary<string, string> { ["intervalSeconds"] = intervalSeconds.ToString() });

            // Return promptly — long-lived work goes on our own task, not the caller's.
            loop = Task.Run(() => RunAsync(cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            context?.LogInformation("Ticker stopping.");
            cts?.Cancel();
            if (loop != null) await Task.WhenAny(loop, Task.Delay(2000, cancellationToken));
        }

        public Task<AdapterStatus> GetStatusAsync()
        {
            // Answered even while a tick is in flight, because the stream is multiplexed.
            var status = new AdapterStatus
            {
                Connected = true,
                State = produced == 0 ? "Idle" : "Connected",
                LastMessageOn = lastMessageOn,
                LastError = lastError
            };
            status.Details["produced"] = produced.ToString();
            status.Details["accepted"] = accepted.ToString();
            status.Details["rejected"] = rejected.ToString();
            status.Details["intervalSeconds"] = intervalSeconds.ToString();
            return Task.FromResult(status);
        }

        // ------------------------------------------------------------------ the push loop

        async Task RunAsync(CancellationToken ct)
        {
            var sequence = 0L;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
                }
                catch (OperationCanceledException) { return; }

                sequence++;
                var payload = Encoding.UTF8.GetBytes(
                    $"{{\"sequence\":{sequence},\"utc\":\"{DateTimeOffset.UtcNow:O}\"}}");

                try
                {
                    produced++;

                    // Wait for the host to persist. A real broker adapter would only commit its
                    // offset after this returns Accepted.
                    var result = await context.PublishAsync(
                        payload,
                        dedupeKey: $"ticker:{context.InstanceKey}:{sequence}",
                        endpoint: "tick",
                        contentType: "application/json",
                        cancellationToken: ct);

                    if (result.Accepted)
                    {
                        accepted++;
                        lastMessageOn = DateTimeOffset.UtcNow;
                        context.Log(AdapterLogLevel.Debug, $"Tick {sequence} accepted as {result.Reference}.");
                    }
                    else
                    {
                        rejected++;
                        lastError = result.Error;
                        context.LogWarning($"Tick {sequence} rejected: {result.Error}");
                    }

                    context.Metric("ticker.published", 1, new Dictionary<string, string> { ["endpoint"] = "tick" });
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    context.LogError($"Tick {sequence} failed.", ex);
                }
            }
        }

        // ------------------------------------------------------------------ commands
        // Public Task / Task<T> methods are discovered by name, exactly as in the classic runner.

        public Task<object> SetInterval(int seconds)
        {
            if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Interval must be positive.");
            intervalSeconds = seconds;
            context.LogInformation($"Interval changed to {seconds}s at runtime.");
            return Task.FromResult<object>(new { intervalSeconds });
        }

        public Task<object> GetCounters() =>
            Task.FromResult<object>(new { produced, accepted, rejected, lastMessageOn });

        /// <summary>
        /// Host-held state, the way a polling receiver keeps its cursor: written through the host
        /// so that it survives a restart and is still there when the next instance comes up —
        /// possibly on another node, possibly as a different process entirely.
        /// </summary>
        public async Task<object> SaveCursor(string value)
        {
            await context.SetStateAsync("cursor", value);
            return new { saved = value };
        }

        public async Task<object> ReadCursor() =>
            new { cursor = await context.GetStateAsync("cursor") };

        public async Task<object> ClearCursor()
        {
            await context.SetStateAsync("cursor", null);
            return new { cleared = true };
        }

        /// <summary>
        /// Per-call configuration, which is what a SHARED instance needs: one process serving many
        /// callers cannot take per-caller settings from its startup values, because those belong to
        /// the process.
        /// </summary>
        public Task<object> ReadValue(string name) => Task.FromResult<object>(new
        {
            invocation = context.InvocationValues.TryGetValue(name, out var v) ? v : null,
            startup = context.StartupValueOf(name),
            resolved = context.ValueOf(name)
        });

        /// <summary>
        /// Reads the same value either side of an await, so a test can prove two concurrent callers
        /// do not see each other's — the failure an ordinary field would have.
        /// </summary>
        public async Task<object> ReadValueSlowly(string name)
        {
            var before = context.ValueOf(name);
            await Task.Delay(300);
            var after = context.ValueOf(name);
            return new { before, after };
        }

        /// <summary>Demonstrates that a command failure comes back as a typed error, not a hang.</summary>
        public Task Explode() => throw new InvalidOperationException("Deliberate failure from the ticker sample.");

        /// <summary>
        /// Blocks for a while, so a caller can force a timeout. Under v1 the late reply would go on
        /// to resolve the NEXT call's completion; here it is discarded, and the following call
        /// still gets its own answer. Also proves the stream is multiplexed: the heartbeat is
        /// answered while this is running.
        /// </summary>
        public async Task<object> Sleep(int seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            return new { sleptSeconds = seconds };
        }
    }
}
