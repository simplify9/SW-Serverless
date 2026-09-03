using System;
using System.Collections.Generic;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// One instance as the node-health view sees it. Two independent sources deliberately:
    /// HOST-OBSERVED figures (memory, CPU, restarts) need no adapter cooperation, so they still
    /// work when the adapter is wedged; ADAPTER-REPORTED ones carry provider detail.
    /// Design doc, section 6.3.
    /// </summary>
    public class InstanceHealth
    {
        public string AdapterId { get; set; }
        public string InstanceKey { get; set; }
        public InstanceState State { get; set; }

        // Host-observed
        public int? ProcessId { get; set; }
        public long WorkingSetBytes { get; set; }
        public double CpuPercent { get; set; }
        public int ThreadCount { get; set; }
        public TimeSpan Uptime { get; set; }
        public int RestartCount { get; set; }
        public int MissedHeartbeats { get; set; }
        public bool Quarantined { get; set; }
        public bool DrainRequested { get; set; }
        public DateTimeOffset? LastHeartbeatOn { get; set; }

        // Adapter-reported
        public bool Connected { get; set; }
        public string ReportedState { get; set; }
        public DateTimeOffset? LastMessageOn { get; set; }
        public long InFlight { get; set; }
        public string LastError { get; set; }
        public IDictionary<string, string> Details { get; set; } = new Dictionary<string, string>();

        public IReadOnlyCollection<string> Diagnostics { get; set; } = Array.Empty<string>();
    }
}
