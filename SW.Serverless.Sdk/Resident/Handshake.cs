using Newtonsoft.Json;

namespace SW.Serverless.Sdk.Resident
{
    /// <summary>
    /// Written by the host as a single JSON line on the child's stdin, immediately after spawn.
    /// It travels here rather than on argv so that nothing secret is visible in `ps aux` —
    /// see the design doc, section 14.3.
    /// </summary>
    public class Handshake
    {
        /// <summary>Unix domain socket path (Linux / macOS).</summary>
        [JsonProperty("socket")] public string Socket { get; set; }

        /// <summary>Named pipe name (Windows). Exactly one of Socket / Pipe is set.</summary>
        [JsonProperty("pipe")] public string Pipe { get; set; }

        /// <summary>One-time token proving this child is the process the host just spawned.</summary>
        [JsonProperty("token")] public string Token { get; set; }

        [JsonProperty("adapterId")] public string AdapterId { get; set; }

        /// <summary>DataSourceId for an exclusive resident, pool slot id for a pooled one.</summary>
        [JsonProperty("instanceKey")] public string InstanceKey { get; set; }

        [JsonProperty("protocol")] public int Protocol { get; set; } = 2;

        public static Handshake Parse(string line) => JsonConvert.DeserializeObject<Handshake>(line);
        public string Serialize() => JsonConvert.SerializeObject(this);
    }
}
