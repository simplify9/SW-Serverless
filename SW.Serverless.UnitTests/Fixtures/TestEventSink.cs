using SW.Serverless.Resident;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests.Fixtures
{
    public record Delivered(string AdapterId, string Endpoint, string DedupeKey, string Body, bool Accepted);

    /// <summary>Controllable stand-in for a real ingest path.</summary>
    public class TestEventSink : IAdapterEventSink
    {
        readonly ConcurrentQueue<Delivered> delivered = new();
        readonly ConcurrentDictionary<string, string> persisted = new();

        long sequence;

        /// <summary>When true every event is rejected, so the adapter must not acknowledge.</summary>
        public bool RejectEverything { get; set; }

        public IReadOnlyList<Delivered> Delivered => delivered.ToArray();
        public int DuplicateCount;

        public Task<EventOutcome> OnEventAsync(InboundEvent e, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetString(e.Payload ?? Array.Empty<byte>());

            if (RejectEverything)
            {
                delivered.Enqueue(new Delivered(e.AdapterId, e.Endpoint, e.DedupeKey, body, false));
                return Task.FromResult(EventOutcome.Rejected("rejected by the test"));
            }

            // Check-then-act was racy: two concurrent deliveries of the same key could both miss
            // and both be persisted, which is precisely the bug a dedupe test exists to catch.
            var reference = $"ref-{Interlocked.Increment(ref sequence)}";

            if (!string.IsNullOrEmpty(e.DedupeKey))
            {
                var winner = persisted.GetOrAdd(e.DedupeKey, reference);
                if (!ReferenceEquals(winner, reference) && winner != reference)
                {
                    Interlocked.Increment(ref DuplicateCount);
                    delivered.Enqueue(new Delivered(e.AdapterId, e.Endpoint, e.DedupeKey, body, true));
                    return Task.FromResult(EventOutcome.Ok(winner));
                }
            }

            delivered.Enqueue(new Delivered(e.AdapterId, e.Endpoint, e.DedupeKey, body, true));
            return Task.FromResult(EventOutcome.Ok(reference));
        }

        public void Clear()
        {
            delivered.Clear();
            persisted.Clear();
            DuplicateCount = 0;
        }
    }
}
