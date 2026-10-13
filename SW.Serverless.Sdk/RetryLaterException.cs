using System;
using System.Globalization;

namespace SW.Serverless.Sdk
{
    /// <summary>
    /// A failure that is worth retrying, but not before <see cref="After"/>: a partner answered
    /// 429 Too Many Requests with a Retry-After, or a quota resets at a known time.
    /// </summary>
    /// <remarks>
    /// Its message starts with <c>{{retryAfter:SECONDS}}</c>, which is the whole of the contract: a
    /// host that reads it, as Bitween does, schedules the retry its policy allows no sooner than
    /// that. Text rather than a protocol field, so it reaches the host the same way from the classic
    /// and the resident runners, and any adapter in any language can say it by starting its error
    /// message that way. A host that doesn't read it sees an ordinary failure.
    /// </remarks>
    public class RetryLaterException : Exception
    {
        public TimeSpan After { get; }

        public RetryLaterException(TimeSpan after, string message, Exception innerException = null)
            : base($"{Prefix(after)} {message}", innerException)
        {
            After = after < TimeSpan.Zero ? TimeSpan.Zero : after;
        }

        /// <summary><c>{{retryAfter:SECONDS}}</c>, whole seconds rounded up.</summary>
        public static string Prefix(TimeSpan after) =>
            $"{{{{retryAfter:{Math.Max(0, (long)Math.Ceiling(after.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}}}}}";
    }
}
