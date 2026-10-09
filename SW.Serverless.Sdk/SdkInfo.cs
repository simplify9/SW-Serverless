using System;
using System.Reflection;

namespace SW.Serverless.Sdk
{
    /// <summary>The version of this SDK, as an adapter reports it in its description and handshake.</summary>
    public static class SdkInfo
    {
        /// <summary>
        /// The release this code is, kept by hand as the host's HostInfo.Baseline is: the publish
        /// workflow stamps the version on the package, and the assembly itself says 1.0.0.0.
        /// </summary>
        public const string Baseline = "10.1.0";

        public static string Version { get; } = Resolve();

        static string Resolve()
        {
            var informational = typeof(SdkInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var core = informational?.Split('+', '-')[0];
            return System.Version.TryParse(core, out var stamped) && stamped > System.Version.Parse(Baseline)
                ? stamped.ToString(3)
                : Baseline;
        }
    }
}
