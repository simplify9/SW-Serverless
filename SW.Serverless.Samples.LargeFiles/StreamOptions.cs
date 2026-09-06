namespace SW.Serverless.Samples.LargeFiles
{
    /// <summary>
    /// Bound straight from startup values by name — <c>ChunkSizeKb</c> as a startup value fills
    /// <c>ChunkSizeKb</c> here. No manual StartupValueOf calls, and no constructor footgun.
    /// </summary>
    public class StreamOptions
    {
        public string Path { get; set; }
        public string ArchivePath { get; set; }
        public string Pattern { get; set; } = "*.bin";

        /// <summary>
        /// The whole point of the sample: memory stays proportional to this, not to file size.
        /// </summary>
        public int ChunkSizeKb { get; set; } = 256;

        public int PollSeconds { get; set; } = 3;

        /// <summary>Slows the stream so progress is watchable in the dashboard.</summary>
        public int ThrottleMsPerChunk { get; set; }
    }
}
