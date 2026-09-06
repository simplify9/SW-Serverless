using System;
using System.Collections.Generic;

namespace SW.Serverless.Sdk.Resident
{
    /// <summary>
    /// What a heartbeat answers with. Liveness alone cannot separate "alive but disconnected"
    /// from "connected but receiving nothing" — see the design doc, section 6.4.
    /// </summary>
    public class AdapterStatus
    {
        public bool Connected { get; set; }

        /// <summary>Free text: Starting, Connected, Idle, Disconnected, Draining, Failed.</summary>
        public string State { get; set; } = "Unknown";

        public DateTimeOffset? LastMessageOn { get; set; }
        public long InFlight { get; set; }
        public string LastError { get; set; }

        /// <summary>Provider-specific detail: partitions, queues, lag, prefetch, ...</summary>
        public IDictionary<string, string> Details { get; } = new Dictionary<string, string>();

        public static AdapterStatus Ok(string state = "Connected") =>
            new AdapterStatus { Connected = true, State = state };
    }
}
