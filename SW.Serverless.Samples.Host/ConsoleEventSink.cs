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
            var body = Encoding.UTF8.GetString(e.Payload);
            if (body.Length > 120) body = body[..120] + "...";

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

        static class Seen
        {
            static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> keys = new();
            public static bool TryAdd(string key) =>
                string.IsNullOrEmpty(key) || keys.TryAdd(key, 0);
        }
    }
}
