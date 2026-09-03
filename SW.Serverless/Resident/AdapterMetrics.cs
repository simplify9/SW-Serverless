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

        public static void Record(string adapterId, string instanceKey, Metric metric)
        {
            Counter<double> counter;
            lock (gate)
            {
                if (!counters.TryGetValue(metric.Name, out counter))
                    counters[metric.Name] = counter = Meter.CreateCounter<double>(metric.Name);
            }

            var tags = new List<KeyValuePair<string, object>>
            {
                new("adapter.id", adapterId),
                new("adapter.instance", instanceKey)
            };
            foreach (var kv in metric.Tags) tags.Add(new KeyValuePair<string, object>(kv.Key, kv.Value));

            counter.Add(metric.Value, tags.ToArray());
        }
    }
}
