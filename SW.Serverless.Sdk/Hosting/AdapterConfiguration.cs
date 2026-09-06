using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SW.Serverless.Sdk.Hosting
{
    /// <summary>
    /// Startup values and cloud metadata as an <c>IConfiguration</c>, so an adapter binds options
    /// the same way any .NET service does. Keys use ':' for nesting, exactly as elsewhere — a
    /// startup value named <c>Retry:MaxAttempts</c> binds to <c>RetryOptions.MaxAttempts</c>.
    /// </summary>
    public static class AdapterConfiguration
    {
        public const string AdapterValuesSection = "AdapterValues";

        /// <summary>
        /// Only environment variables with this prefix are bound, and the prefix is stripped:
        /// SWSL_ChunkSizeKb becomes ChunkSizeKb.
        ///
        /// Binding the environment unprefixed is a trap. A startup value called Path binds to
        /// PATH, Home binds to HOME, User to USER — the adapter reads the machine's environment
        /// instead of its own configuration, and the failure looks nothing like its cause.
        /// </summary>
        public const string EnvironmentPrefix = "SWSL_";

        public static IConfigurationRoot Build(
            IReadOnlyDictionary<string, string> startupValues,
            IReadOnlyDictionary<string, string> adapterValues)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in startupValues ?? new Dictionary<string, string>())
                values[kv.Key] = kv.Value;

            // Cloud metadata is namespaced so it can never shadow a startup value.
            foreach (var kv in adapterValues ?? new Dictionary<string, string>())
                values[$"{AdapterValuesSection}:{kv.Key}"] = kv.Value;

            return new ConfigurationBuilder()
                // Environment first, so the host's startup values always win. They are the more
                // specific source and the one an operator edits at runtime.
                .AddEnvironmentVariables(EnvironmentPrefix)
                .AddInMemoryCollection(values.Select(kv =>
                    new KeyValuePair<string, string>(kv.Key, kv.Value)))
                .Build();
        }
    }
}
