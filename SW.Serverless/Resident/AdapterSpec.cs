using System.Collections.Generic;

namespace SW.Serverless.Resident
{
    public class AdapterSpec
    {
        /// <summary>Adapter id, as used for the cloud-storage lookup.</summary>
        public string AdapterId { get; set; }

        /// <summary>
        /// DataSourceId for an exclusive instance. Left null for a pooled one, where the pool
        /// assigns a slot id.
        /// </summary>
        public string InstanceKey { get; set; }

        /// <summary>
        /// Path to the entry assembly. When null the configured locator resolves it — which is
        /// where the existing S3 download-and-extract step plugs in unchanged (design doc 15.2).
        /// </summary>
        public string EntryAssemblyPath { get; set; }

        /// <summary>Executable to launch. Defaults to `dotnet`; set for self-contained or non-.NET adapters.</summary>
        public string Executable { get; set; }

        /// <summary>
        /// Which warm pool a POOLED rental belongs to. Left null the host derives one from the
        /// adapter id and a hash of <see cref="StartupValues"/>, so that two configurations of the
        /// same adapter never share processes. Set it when the caller has a better name for the
        /// grouping — a data source id, say — than the settings happen to hash to.
        ///
        /// Ignored for an exclusive instance, which is keyed by <see cref="InstanceKey"/>.
        /// </summary>
        public string PoolKey { get; set; }

        /// <summary>Configuration and credentials. Sent over the stream, never on argv.</summary>
        public IDictionary<string, string> StartupValues { get; set; } = new Dictionary<string, string>();

        /// <summary>Cloud-object metadata: Protocol, Lifecycle, Launcher, MaxInFlight, ...</summary>
        public IDictionary<string, string> AdapterValues { get; set; } = new Dictionary<string, string>();

        public long SoftMemoryLimitBytes { get; set; }
        public long HardMemoryLimitBytes { get; set; }

        /// <summary>
        /// Sustained CPU ceiling as a percentage of the whole machine. Zero uses the host default. See
        /// <see cref="ResourceLimits.CpuPercentLimit"/> for why it is sustained rather than
        /// instantaneous.
        /// </summary>
        public double CpuPercentLimit { get; set; }

        /// <summary>Consecutive over-limit samples before the CPU ceiling trips. Zero uses the host default.</summary>
        public int CpuLimitSamples { get; set; }
    }
}
