
namespace SW.Serverless.Sdk
{
    public static class Constants
    {
        public const string Delimiter = "#!#";
        public const string NullIdentifier = "{{null}}";
        public const string NewLineIdentifier = "{{newline}}";
        public const string ErrorIdentifier = "{{error}}";
        public const string QuitCommand = "{{quit}}";
        public const string ExpectedCommand = "{{expected}}";
        public const string LogInformationIdentifier = "{{log.information}}";
        public const string LogWarningIdentifier = "{{log.warning}}";
        public const string LogErrorIdentifier = "{{log.error}}";
        public const string CorrelationIdName = "CorrelationId";

        /// <summary>
        /// Passed instead of the three base64 arguments when the host sends them on stdin: the
        /// options, the startup values and the adapter values, one line each, before the first
        /// command. Startup values hold passwords and keys, and anything on the command line can
        /// be read by every process on the machine.
        /// </summary>
        public const string ValuesOnStdinFlag = "--values-on-stdin";

        /// <summary>The first SDK release that reads <see cref="ValuesOnStdinFlag"/>.</summary>
        public const string ValuesOnStdinSince = "10.1.0";

    }
}
