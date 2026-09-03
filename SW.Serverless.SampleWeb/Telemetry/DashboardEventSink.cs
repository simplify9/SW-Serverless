using Microsoft.Extensions.Logging;
using SW.Serverless.Resident;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.SampleWeb.Telemetry
{
    /// <summary>
    /// Stands in for a real ingest path. Bitween would persist an Xchange and return its id here;
    /// the adapter does not acknowledge its broker until this returns Accepted.
    /// </summary>
    public class DashboardEventSink : IAdapterEventSink
    {
        readonly DashboardState state;
        readonly ILogger<DashboardEventSink> logger;
        readonly ConcurrentDictionary<string, string> seen = new();

        long sequence;

        public DashboardEventSink(DashboardState state, ILogger<DashboardEventSink> logger)
        {
            this.state = state;
            this.logger = logger;
        }

        public Task<EventOutcome> OnEventAsync(InboundEvent e, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetString(e.Payload ?? Array.Empty<byte>());
            var preview = body.Length > 200 ? body[..200] + "..." : body;

            // At-least-once delivery is not optional, so neither is this.
            if (!string.IsNullOrEmpty(e.DedupeKey) && seen.TryGetValue(e.DedupeKey, out var already))
            {
                Interlocked.Increment(ref state.Duplicates);
                state.Events.Add(new EventRow(DateTimeOffset.Now, e.AdapterId, e.Endpoint,
                    e.DedupeKey, e.Payload?.Length ?? 0, preview, "duplicate", already));
                logger.LogWarning("Duplicate {Key} — already persisted as {Reference}.", e.DedupeKey, already);
                return Task.FromResult(EventOutcome.Ok(already));
            }

            // Failure injection: prove the adapter leaves the message for redelivery.
            if (Interlocked.Decrement(ref state.RejectNext) >= 0)
            {
                Interlocked.Increment(ref state.Rejected);
                state.Events.Add(new EventRow(DateTimeOffset.Now, e.AdapterId, e.Endpoint,
                    e.DedupeKey, e.Payload?.Length ?? 0, preview, "rejected", null));
                return Task.FromResult(EventOutcome.Rejected("Rejected on purpose from the dashboard."));
            }
            Interlocked.Exchange(ref state.RejectNext, Math.Max(0, state.RejectNext));

            var reference = $"xch-{Interlocked.Increment(ref sequence):00000}";
            if (!string.IsNullOrEmpty(e.DedupeKey)) seen[e.DedupeKey] = reference;

            Interlocked.Increment(ref state.Accepted);
            state.Events.Add(new EventRow(DateTimeOffset.Now, e.AdapterId, e.Endpoint,
                e.DedupeKey, e.Payload?.Length ?? 0, preview, "accepted", reference));

            return Task.FromResult(EventOutcome.Ok(reference));
        }
    }
}
