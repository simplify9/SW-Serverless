using System;

namespace SW.Serverless.Tooling
{
    /// <summary>
    /// The ceilings an adapter run by <see cref="LocalAdapterHost"/> runs under — what a tool that
    /// builds and tries adapters on a shared server puts on drafts it has no reason to trust yet.
    /// Zero on a ceiling means none; with no limits at all an adapter runs as it always has.
    /// </summary>
    public sealed class LocalAdapterLimits
    {
        /// <summary>
        /// The most memory the adapter's process may use, in bytes. It is the host's hard ceiling: the
        /// process is killed when a sample finds it over, and the call it was running fails saying so.
        /// The runtime enforces it too where it can — .NET's GC heap limit, Node's
        /// --max-old-space-size, RLIMIT_DATA for Python on Linux — so an allocation past it usually
        /// fails inside the adapter first. 0 for none.
        /// </summary>
        public long MemoryLimitBytes { get; set; }

        /// <summary>
        /// Sustained CPU ceiling, as a percentage of the WHOLE machine, as the resident host measures
        /// it: one core flat out on an eight-core machine is 12.5%. Held for
        /// <see cref="CpuLimitSamples"/> samples in a row, the adapter is asked to stop and, if it does
        /// not within a few samples, killed; the call it was running fails saying so. 0 for none.
        /// </summary>
        public double CpuPercentLimit { get; set; }

        /// <summary>Samples in a row over <see cref="CpuPercentLimit"/> before it trips.</summary>
        public int CpuLimitSamples { get; set; } = 3;

        /// <summary>
        /// How often the process is sampled. A second by default, so a short try is caught rather
        /// than finishing between two of a long-running host's samples.
        /// </summary>
        public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>True when neither ceiling is set.</summary>
        public bool IsEmpty => MemoryLimitBytes <= 0 && CpuPercentLimit <= 0;
    }
}
