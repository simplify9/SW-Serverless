using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// Turns an AdapterSpec into something launchable. The default resolves an explicit
    /// EntryAssemblyPath first, and otherwise installs from cloud storage — the same
    /// download-and-extract step the classic path already uses.
    /// </summary>
    public interface IResidentAdapterLocator
    {
        Task<ResolvedAdapter> ResolveAsync(AdapterSpec spec, CancellationToken cancellationToken = default);
    }

    public class ResolvedAdapter
    {
        public string EntryAssemblyPath { get; set; }
        public string Executable { get; set; }

        /// <summary>The runtime it runs on, from its manifest; empty means dotnet.</summary>
        public string Runtime { get; set; }

        public System.Collections.Generic.IDictionary<string, string> AdapterValues { get; set; }

        /// <summary>The extraction directory, when installed from storage. Held while the adapter runs.</summary>
        public string Directory { get; set; }
    }

    internal class DefaultResidentAdapterLocator : IResidentAdapterLocator
    {
        readonly System.IServiceProvider services;

        // The installer, and the storage and options it needs, only when an adapter is installed
        // from storage: a host that gives every adapter by path registers neither AddServerless nor
        // storage, and must still start.
        public DefaultResidentAdapterLocator(System.IServiceProvider services) => this.services = services;

        public async Task<ResolvedAdapter> ResolveAsync(AdapterSpec spec, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(spec.EntryAssemblyPath))
                return new ResolvedAdapter
                {
                    EntryAssemblyPath = spec.EntryAssemblyPath,
                    Executable = spec.Executable,
                    Runtime = spec.Runtime,
                    AdapterValues = spec.AdapterValues
                };

            var installer = services.GetService(typeof(AdapterInstaller)) as AdapterInstaller
                            ?? throw new System.InvalidOperationException(
                                $"Adapter '{spec.AdapterId}' has no EntryAssemblyPath, and installing it from storage needs AddServerless and a cloud files service.");
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
                Directory = installed.Directory,
                Executable = spec.Executable ?? (values.TryGetValue("Executable", out var exe) ? exe : null),
                Runtime = installed.Runtime,
                AdapterValues = values
            };
        }
    }
}
