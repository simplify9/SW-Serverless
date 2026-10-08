using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.Contract.Catalog
{
    /// <summary>Reads and writes catalog entries in the same storage as the packages.</summary>
    public class AdapterCatalogStore
    {
        readonly ICloudFilesService files;
        readonly string root;

        /// <param name="root">The adapters root, "adapters" unless a host configured another.</param>
        public AdapterCatalogStore(ICloudFilesService files, string root = "adapters")
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.root = string.IsNullOrWhiteSpace(root) ? "adapters" : root.TrimEnd('/');
        }

        public string Root => root;

        /// <summary>The adapter's entry, or null when it has none — every adapter published before the catalog.</summary>
        public async Task<AdapterCatalogEntry> GetAsync(string adapterId)
        {
            var key = AdapterCatalogPaths.Catalog(root, adapterId);

            // Listed first rather than opened and caught: what a missing key throws differs by
            // storage provider, and a listing answers the same way on all of them.
            var exists = (await files.ListAsync(key)).Any(f => string.Equals(f.Key, key, StringComparison.Ordinal));
            if (!exists) return null;

            using var stream = await files.OpenReadAsync(key);
            using var reader = new StreamReader(stream);
            return AdapterCatalogEntry.Parse(await reader.ReadToEndAsync());
        }

        /// <summary>Every adapter with a catalog entry. An entry that cannot be read is skipped, not fatal.</summary>
        public async Task<IReadOnlyList<AdapterCatalogEntry>> ListAsync()
        {
            var entries = new List<AdapterCatalogEntry>();
            foreach (var file in await files.ListAsync(AdapterCatalogPaths.CatalogRoot(root)))
            {
                if (!file.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var stream = await files.OpenReadAsync(file.Key);
                    using var reader = new StreamReader(stream);
                    entries.Add(AdapterCatalogEntry.Parse(await reader.ReadToEndAsync()));
                }
                catch
                {
                    // One damaged entry must not empty the whole catalog.
                }
            }
            return entries;
        }

        public async Task SaveAsync(AdapterCatalogEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry?.Id)) throw new ArgumentException("A catalog entry needs an id.", nameof(entry));
            entry.UpdatedOn = DateTimeOffset.UtcNow;
            await files.WriteTextAsync(entry.ToJson(), new WriteFileSettings
            {
                Key = AdapterCatalogPaths.Catalog(root, entry.Id),
                ContentType = "application/json"
            });
        }
    }
}
