namespace SW.Serverless.Resident
{
    /// <summary>
    /// The resident protocol versions this host speaks. The handshake offers the highest; the
    /// adapter answers in Hello with the one it will use, and any version in the range is accepted.
    /// Moving to a new version therefore raises <see cref="Max"/> and keeps <see cref="Min"/>, so
    /// every adapter already deployed keeps working — the adapter used to require an exact match,
    /// which would have stranded the whole fleet at the first bump.
    /// </summary>
    public static class ProtocolVersions
    {
        public const int Min = 2;
        public const int Max = 2;

        public static bool Supports(int version) => version >= Min && version <= Max;
    }
}
