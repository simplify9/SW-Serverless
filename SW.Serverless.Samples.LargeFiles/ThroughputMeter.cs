using System;
using System.Diagnostics;

namespace SW.Serverless.Samples.LargeFiles
{
    /// <summary>Rolling throughput, so the dashboard can show MB/s and a real ETA.</summary>
    public class ThroughputMeter
    {
        readonly Stopwatch clock = new();
        long bytes;

        public void Start()
        {
            clock.Restart();
            bytes = 0;
        }

        public void Add(int count) => bytes += count;

        public long Bytes => bytes;
        public TimeSpan Elapsed => clock.Elapsed;

        public double MegabytesPerSecond => clock.Elapsed.TotalSeconds <= 0
            ? 0
            : Math.Round(bytes / 1024d / 1024d / clock.Elapsed.TotalSeconds, 2);

        public TimeSpan? Remaining(long total)
        {
            var rate = MegabytesPerSecond * 1024 * 1024;
            if (rate <= 0 || total <= bytes) return null;
            return TimeSpan.FromSeconds((total - bytes) / rate);
        }
    }
}
