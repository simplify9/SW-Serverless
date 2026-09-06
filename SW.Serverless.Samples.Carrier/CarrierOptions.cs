namespace SW.Serverless.Samples.Carrier
{
    /// <summary>Bound straight from startup values — the same keys a Traxis agent's Settings hold.</summary>
    public class CarrierOptions
    {
        /// <summary>The carrier's gRPC endpoint.</summary>
        public string BaseUrl { get; set; } = "http://localhost:5200";

        /// <summary>Credentials. Declared private, so a UI masks them and never echoes them back.</summary>
        public string Account { get; set; }
        public string ApiKey { get; set; }

        public string DefaultService { get; set; } = "STANDARD";
        public int TimeoutSeconds { get; set; } = 20;

        /// <summary>Transient upstream failures are the carrier's normal, not an exception.</summary>
        public int MaxAttempts { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 200;

    /// <summary>
    /// Simulates a reset that takes real work — flushing a buffer, closing a scope. Also what
    /// makes the pool's hand-over ordering testable rather than a matter of timing.
    /// </summary>
    public int ResetDelayMs { get; set; }
    }
}
