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
        }
    }
}
