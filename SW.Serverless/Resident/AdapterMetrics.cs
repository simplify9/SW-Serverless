using SW.Serverless.Contract;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// Adapter-reported metrics republished on System.Diagnostics.Metrics, so they export
    /// wherever the host already exports (design doc 6.3).
    /// </summary>
    public static class AdapterMetrics
    {
        public static readonly Meter Meter = new("SW.Serverless.Adapters", "1.0.0");

        static readonly Dictionary<string, Counter<double>> counters = new();
        static readonly object gate = new();

        /// <summary>
        /// Instrument names and tags come from the adapter, so both are bounded here. An adapter
        /// that derives a metric name or a tag from message content would otherwise create an
        /// unbounded number of time series and take the metrics backend with it.
        /// </summary>
        const int MaxInstruments = 200;
        const int MaxTagsPerMetric = 10;
        const int MaxTagValueLength = 120;

        public static void Record(string adapterId, string instanceKey, Metric metric)
        {
            if (string.IsNullOrWhiteSpace(metric?.Name)) return;

            Counter<double> counter;
            lock (gate)
            {
                if (!counters.TryGetValue(metric.Name, out counter))
                {
                    if (counters.Count >= MaxInstruments) return;
                    counters[metric.Name] = counter = Meter.CreateCounter<double>(metric.Name);
                }
            }

            var tags = new List<KeyValuePair<string, object>>
            {
                new("adapter.id", adapterId),
                new("adapter.instance", instanceKey)
            };
            foreach (var kv in metric.Tags)
            {
                if (tags.Count >= MaxTagsPerMetric + 2) break;
                var value = kv.Value ?? "";
                if (value.Length > MaxTagValueLength) value = value[..MaxTagValueLength];
                tags.Add(new KeyValuePair<string, object>(kv.Key, value));
            }

            counter.Add(metric.Value, tags.ToArray());
        }
    }
}
