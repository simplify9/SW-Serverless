using Microsoft.Extensions.Logging;
using SW.Serverless.Resident;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Host
{
    /// <summary>
    /// Stands in for Bitween's ingest path. In the real host this persists an Xchange, writes the
    /// payload to cloud storage, commits, and returns the Xchange id — and only then does the
    /// adapter acknowledge its broker.
    /// </summary>
    public class ConsoleEventSink : IAdapterEventSink
    {
        readonly ILogger<ConsoleEventSink> logger;
        long sequence;

        public ConsoleEventSink(ILogger<ConsoleEventSink> logger) => this.logger = logger;

        public Task<EventOutcome> OnEventAsync(InboundEvent e, CancellationToken cancellationToken)
        {
            var body = Preview(e.Payload, 120);

            // Reject anything already seen: at-least-once delivery means this is not optional.
            if (!Seen.TryAdd(e.DedupeKey))
            {
                logger.LogWarning("DUPLICATE  {Adapter}/{Endpoint}  key={Key} — already persisted, acking without re-persisting.",
                    e.AdapterId, e.Endpoint, e.DedupeKey);
                return Task.FromResult(EventOutcome.Ok("duplicate"));
            }

            var reference = $"xch-{Interlocked.Increment(ref sequence):00000}";
            logger.LogInformation("EVENT      {Adapter}/{Endpoint}  {Reference}  {Bytes}B  {Body}",
                e.AdapterId, e.Endpoint, reference, e.Payload.Length, body);

            return Task.FromResult(EventOutcome.Ok(reference));
        }

        /// <summary>
        /// Decodes only what the preview shows. Decoding the whole payload first allocates a string
        /// the size of every inbound event, and one large event could then exhaust the host.
        /// </summary>
        static string Preview(byte[] payload, int chars)
        {
            if (payload is not { Length: > 0 }) return "";

            var bytes = Math.Min(payload.Length, chars * 4);      // UTF-8 is at most 4 bytes a char
            var text = Encoding.UTF8.GetString(payload, 0, bytes);
            if (text.Length > chars) text = text[..chars];

            return text.Length < chars && bytes == payload.Length ? text : text + "...";
        }

        /// <summary>
        /// Keys expire rather than accumulating for the process lifetime — this host runs
        /// indefinitely, and a steady stream of distinct keys would otherwise grow until it dies.
        /// The window has to cover the source's redelivery horizon; a real host would keep this in
        /// a database with a pruning job, which is exactly what Bitween does.
        /// </summary>
        static class Seen
        {
            static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
            static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> keys = new();
            static long nextSweepTicks;

            public static bool TryAdd(string key)
            {
                if (string.IsNullOrEmpty(key)) return true;

                Sweep();
                var now = DateTimeOffset.UtcNow;

                if (keys.TryAdd(key, now)) return true;

                // An entry the sweep has not reached yet is expired, not a duplicate.
                return keys.TryGetValue(key, out var at)
                       && now - at > Retention
                       && keys.TryUpdate(key, now, at);
            }

            static void Sweep()
            {
                var now = DateTimeOffset.UtcNow;
                var due = Interlocked.Read(ref nextSweepTicks);
                if (now.UtcTicks < due) return;
                if (Interlocked.CompareExchange(ref nextSweepTicks,
                        now.AddSeconds(30).UtcTicks, due) != due) return;

                foreach (var pair in keys)
                    if (now - pair.Value > Retention)
                        keys.TryRemove(pair);
            }
        }
    }
}
