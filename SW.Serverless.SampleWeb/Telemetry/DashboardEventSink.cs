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
        // Bounded by age, not unbounded for the process lifetime: ten minutes comfortably covers
        // the redelivery horizon of everything the samples connect to.
        readonly DedupeWindow seen = new(TimeSpan.FromMinutes(10));

        long sequence;

        public DashboardEventSink(DashboardState state, ILogger<DashboardEventSink> logger)
        {
            this.state = state;
            this.logger = logger;
        }

        public Task<EventOutcome> OnEventAsync(InboundEvent e, CancellationToken cancellationToken)
        {
            state.Tick();

            var preview = Preview(e.Payload, 200);

            var reference = $"xch-{Interlocked.Increment(ref sequence):00000}";
            var keyed = !string.IsNullOrEmpty(e.DedupeKey);

            // At-least-once delivery is not optional, so neither is this. The claim and the lookup
            // have to be one step: two concurrent redeliveries of the same message must not both
            // pass a read-only check and both be accepted.
            if (keyed && !seen.TryClaim(e.DedupeKey, reference, out var already))
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
                // The event goes back to the source, so give up the claim — its redelivery is a
                // fresh attempt and must not be swallowed as a duplicate.
                if (keyed) seen.Release(e.DedupeKey);

                Interlocked.Increment(ref state.Rejected);
                state.Events.Add(new EventRow(DateTimeOffset.Now, e.AdapterId, e.Endpoint,
                    e.DedupeKey, e.Payload?.Length ?? 0, preview, "rejected", null));
                return Task.FromResult(EventOutcome.Rejected("Rejected on purpose from the dashboard."));
            }
            Interlocked.Exchange(ref state.RejectNext, Math.Max(0, state.RejectNext));

            Interlocked.Increment(ref state.Accepted);
            if (!state.FeedPaused)
                state.Events.Add(new EventRow(DateTimeOffset.Now, e.AdapterId, e.Endpoint,
                    e.DedupeKey, e.Payload?.Length ?? 0, preview, "accepted", reference));

            return Task.FromResult(EventOutcome.Ok(reference));
        }

        /// <summary>
        /// Decodes only as much as the preview can show. Decoding the whole payload first would
        /// allocate a string the size of every inbound event just to throw most of it away — and a
        /// single large event could take the host down.
        /// </summary>
        static string Preview(byte[] payload, int chars)
        {
            if (payload is not { Length: > 0 }) return "";

            var bytes = Math.Min(payload.Length, chars * 4);      // UTF-8 is at most 4 bytes a char
            var text = Encoding.UTF8.GetString(payload, 0, bytes);
            if (text.Length > chars) text = text[..chars];

            return text.Length < chars && bytes == payload.Length ? text : text + "...";
        }
    }
}
