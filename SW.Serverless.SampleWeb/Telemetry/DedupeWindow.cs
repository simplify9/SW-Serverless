using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SW.Serverless.SampleWeb.Telemetry
{
    /// <summary>
    /// A dedupe table with a retention window, which is the shape a real one has too. Bitween keeps
    /// this in the database with a pruning job; a sample keeps it in memory — but not for ever, or a
    /// long-lived host with a steady stream of distinct keys simply grows until it dies.
    ///
    /// The window has to cover the source's redelivery horizon. Evicting sooner than that lets a
    /// late redelivery through as if it were new, which is why this expires by age rather than by
    /// capacity.
    /// </summary>
    public sealed class DedupeWindow
    {
        readonly ConcurrentDictionary<string, Entry> entries = new();
        readonly TimeSpan retention;
        long nextSweepTicks;

        public DedupeWindow(TimeSpan retention) => this.retention = retention;

        public int Count => entries.Count;

        /// <summary>
        /// Claims <paramref name="key"/> atomically. Returns false when it was already claimed, and
        /// hands back the reference the first claimant recorded. Check-then-act would let two
        /// concurrent redeliveries of one message both be accepted.
        /// </summary>
        public bool TryClaim(string key, string reference, out string existing)
        {
            Sweep();

            var mine = new Entry(reference, DateTimeOffset.UtcNow);
            var winner = entries.GetOrAdd(key, mine);

            if (ReferenceEquals(winner, mine)) { existing = reference; return true; }

            // An entry older than the window is a leftover the sweep has not reached yet; treat it
            // as expired rather than as a duplicate.
            if (DateTimeOffset.UtcNow - winner.At > retention &&
                entries.TryUpdate(key, mine, winner))
            {
                existing = reference;
                return true;
            }

            existing = winner.Reference;
            return false;
        }

        /// <summary>
        /// Releases a claim. Used when the event was rejected and goes back to the source: its
        /// redelivery is a fresh attempt, not a duplicate.
        /// </summary>
        public void Release(string key) => entries.TryRemove(key, out _);

        void Sweep()
        {
            var now = DateTimeOffset.UtcNow;
            var due = Interlocked.Read(ref nextSweepTicks);
            if (now.UtcTicks < due) return;
            if (Interlocked.CompareExchange(ref nextSweepTicks,
                    now.AddSeconds(30).UtcTicks, due) != due) return;

            foreach (var pair in entries)
                if (now - pair.Value.At > retention)
                    entries.TryRemove(pair);
        }

        sealed class Entry
        {
            public readonly string Reference;
            public readonly DateTimeOffset At;
            public Entry(string reference, DateTimeOffset at) { Reference = reference; At = at; }
        }
    }
}
