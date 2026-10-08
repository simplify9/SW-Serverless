using System;
using System.Reflection;

namespace SW.Serverless
{
    /// <summary>The version of this host, for an adapter manifest's <c>compatibility.minHostVersion</c>.</summary>
    public static class HostInfo
    {
        /// <summary>
        /// The release this code is, kept by hand. The published assemblies carry 1.0.0.0 because
        /// the publish workflow stamps the version on the package, not on the build, so the
        /// assembly cannot say which release it is. Raise it with every release that adds something
        /// an adapter could depend on.
        /// </summary>
        public const string Baseline = "10.0.2";

        public static Version Version { get; } = Resolve();

        static Version Resolve()
        {
            var informational = typeof(HostInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var core = informational?.Split('+', '-')[0];
            var baseline = System.Version.Parse(Baseline);
            return System.Version.TryParse(core, out var stamped) && stamped > baseline ? stamped : baseline;
        }

        /// <summary>True when <paramref name="minimum"/> is empty, unreadable, or not above this host.</summary>
        public static bool Satisfies(string minimum)
        {
            if (string.IsNullOrWhiteSpace(minimum)) return true;
            var core = minimum.Trim().TrimStart('v', 'V').Split('+', '-')[0];
            return !System.Version.TryParse(core, out var required) || required <= Version;
        }
    }
}
