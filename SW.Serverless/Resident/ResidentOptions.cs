using System;

namespace SW.Serverless.Resident
{
    public class ResidentOptions
    {
        /// <summary>Unix socket path (Unix) — kept short because macOS caps sun_path at ~104 bytes.</summary>
        public string SocketPath { get; set; } = $"/tmp/swsl-{Environment.ProcessId}.sock";

        /// <summary>Named pipe name (Windows).</summary>
        public string PipeName { get; set; } = $"swsl-{Environment.ProcessId}";

        /// <summary>Credit window: how many events one instance may have unacknowledged.</summary>
        public int MaxInFlight { get; set; } = 16;

        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan InvokeTimeout { get; set; } = TimeSpan.FromSeconds(300);
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Missed heartbeats before the supervisor restarts the instance.</summary>
        public int MissedHeartbeatsBeforeRestart { get; set; } = 3;

        /// <summary>Stderr lines kept per instance for crash forensics (design doc 6.7).</summary>
        public int DiagnosticBufferLines { get; set; } = 200;

        /// <summary>Restarts allowed inside CrashLoopWindow before the instance is quarantined.</summary>
        public int CrashLoopThreshold { get; set; } = 5;
        public TimeSpan CrashLoopWindow { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Soft RSS ceiling; the watchdog asks the adapter to drain (design doc 11.1).</summary>
        public long SoftMemoryLimitBytes { get; set; } = 0;
        public long HardMemoryLimitBytes { get; set; } = 0;

        /// <summary>Workstation GC by default: server GC costs a heap and a thread per core.</summary>
        public bool UseWorkstationGc { get; set; } = true;
    }
}
