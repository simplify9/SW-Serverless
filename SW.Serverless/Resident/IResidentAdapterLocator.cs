using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// Turns an AdapterSpec into something launchable. The default resolves an explicit
    /// EntryAssemblyPath first, and otherwise installs from cloud storage — the same
    /// download-and-extract step the classic path already uses (design doc 15.2).
    /// </summary>
    public interface IResidentAdapterLocator
    {
        Task<ResolvedAdapter> ResolveAsync(AdapterSpec spec, CancellationToken cancellationToken = default);
    }

    public class ResolvedAdapter
    {
        public string EntryAssemblyPath { get; set; }
        public string Executable { get; set; }
        public System.Collections.Generic.IDictionary<string, string> AdapterValues { get; set; }
    }

    internal class DefaultResidentAdapterLocator : IResidentAdapterLocator
    {
        readonly AdapterInstaller installer;

        public DefaultResidentAdapterLocator(AdapterInstaller installer) => this.installer = installer;

        public async Task<ResolvedAdapter> ResolveAsync(AdapterSpec spec, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(spec.EntryAssemblyPath))
                return new ResolvedAdapter
                {
                    EntryAssemblyPath = spec.EntryAssemblyPath,
                    Executable = spec.Executable,
                    AdapterValues = spec.AdapterValues
                };

            var installed = await installer.InstallAsync(spec.AdapterId);

            // Cloud metadata is where Protocol / Lifecycle / Launcher / MaxInFlight live, so an
            // adapter declares its own runtime shape rather than the caller guessing.
            var values = new System.Collections.Generic.Dictionary<string, string>(
                installed.AdapterValues, System.StringComparer.OrdinalIgnoreCase);
            if (spec.AdapterValues != null)
                foreach (var kv in spec.AdapterValues) values[kv.Key] = kv.Value;

            return new ResolvedAdapter
            {
                EntryAssemblyPath = installed.LocalPath,
                Executable = spec.Executable ?? (values.TryGetValue("Executable", out var exe) ? exe : null),
                AdapterValues = values
            };
        }
    }
}
