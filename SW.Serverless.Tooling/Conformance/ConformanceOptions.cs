using System;
using System.Collections.Generic;

namespace SW.Serverless.Tooling.Conformance
{
    public class ConformanceOptions
    {
        /// <summary>The adapter's package, unpacked: its runnable files with adapter.json at the root.</summary>
        public string PackageDirectory { get; set; }

        /// <summary>The settings to run it with — the adapter calls whatever these point at.</summary>
        public IDictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Let a receiver's DeleteFile run. Off by default: it removes or moves a real file at the
        /// source the settings point at.
        /// </summary>
        public bool AllowDelete { get; set; }

        /// <summary>Contracts beyond those the kit carries, e.g. a newer version from a file.</summary>
        public IList<ContractDocument> Contracts { get; set; } = new List<ContractDocument>();

        /// <summary>How long one call may take.</summary>
        public int CommandTimeoutSeconds { get; set; } = 60;

        /// <summary>Where the kit's temporary host keeps its files.</summary>
        public string WorkDirectory { get; set; }

        /// <summary>Where the host finds python3, node and dotnet.</summary>
        public Runtimes.AdapterRuntimeOptions Runtimes { get; set; } = new();

        /// <summary>
        /// Memory and CPU ceilings to run the adapter under while it is checked; null (the default)
        /// for none. A call that crosses one fails its check with the limit named.
        /// </summary>
        public LocalAdapterLimits Limits { get; set; }

        /// <summary>Progress, one line at a time.</summary>
        public Action<string> Log { get; set; } = _ => { };
    }
}
