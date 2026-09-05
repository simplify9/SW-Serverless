using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SW.Serverless.SampleWeb.Telemetry
{
    public record EventRow(DateTimeOffset On, string AdapterId, string Endpoint, string DedupeKey,
        int Bytes, string Body, string Outcome, string Reference);

    public record LogRow(DateTimeOffset On, string AdapterId, string Level, string Message, string Exception);

    public record MetricRow(string Name, string AdapterId, double Value, DateTimeOffset LastOn);

    /// <summary>
    /// Everything the dashboard renders, collected from the three channels the design separates:
    /// events through IAdapterEventSink, logs through ILogger, metrics through
    /// System.Diagnostics.Metrics. Nothing here is invented for the demo.
    /// </summary>
    public class DashboardState
    {
        public Ring<EventRow> Events { get; } = new(300);
        public Ring<LogRow> Logs { get; } = new(500);

        readonly ConcurrentDictionary<string, MetricRow> metrics = new();

        /// <summary>Failure injection: reject the next N events to demonstrate redelivery.</summary>
        public int RejectNext;

        public long Accepted, Rejected, Duplicates;

        /// <summary>Pausing only stops the UI feed; events keep being persisted and acked.</summary>
        public bool FeedPaused;

        readonly Queue<DateTimeOffset> recent = new();
        readonly object rateGate = new();

        public void Tick()
        {
            var now = DateTimeOffset.UtcNow;
            lock (rateGate)
            {
                recent.Enqueue(now);
                while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromSeconds(10))
                    recent.Dequeue();
            }
        }

        /// <summary>Events per second over a rolling ten seconds.</summary>
        public double RatePerSecond
        {
            get
            {
                lock (rateGate)
                {
                    if (recent.Count < 2) return 0;
                    var span = (DateTimeOffset.UtcNow - recent.Peek()).TotalSeconds;
                    return span <= 0 ? 0 : Math.Round(recent.Count / span, 1);
                }
            }
        }

        /// <summary>Ten one-second buckets, oldest first — enough for a sparkline.</summary>
        public int[] RateHistogram()
        {
            var now = DateTimeOffset.UtcNow;
            var buckets = new int[10];
            lock (rateGate)
                foreach (var at in recent)
                {
                    var age = (int)(now - at).TotalSeconds;
                    if (age is >= 0 and < 10) buckets[9 - age]++;
                }
            return buckets;
        }

        public void RecordMetric(string name, string adapterId, double value)
        {
            var key = $"{name}|{adapterId}";
            metrics.AddOrUpdate(key,
                _ => new MetricRow(name, adapterId, value, DateTimeOffset.UtcNow),
                (_, existing) => existing with { Value = existing.Value + value, LastOn = DateTimeOffset.UtcNow });
        }

        public IReadOnlyList<MetricRow> Metrics() =>
            metrics.Values.OrderBy(m => m.AdapterId).ThenBy(m => m.Name).ToList();

        public void Reset()
        {
            Events.Clear();
            Logs.Clear();
            metrics.Clear();
            Accepted = Rejected = Duplicates = 0;
            lock (rateGate) recent.Clear();
        }
    }
}
