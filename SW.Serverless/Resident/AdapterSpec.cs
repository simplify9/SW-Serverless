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

        /// <summary>Configuration and credentials. Sent over the stream, never on argv.</summary>
        public IDictionary<string, string> StartupValues { get; set; } = new Dictionary<string, string>();

        /// <summary>Cloud-object metadata: Protocol, Lifecycle, Launcher, MaxInFlight, ...</summary>
        public IDictionary<string, string> AdapterValues { get; set; } = new Dictionary<string, string>();

        public long SoftMemoryLimitBytes { get; set; }
        public long HardMemoryLimitBytes { get; set; }
    }
}
