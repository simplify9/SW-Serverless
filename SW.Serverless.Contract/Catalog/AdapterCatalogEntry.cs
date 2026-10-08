using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SW.Serverless.Contract.Catalog
{
    /// <summary>
    /// Everything published of one adapter: which version is current, every version with its
    /// manifest, and the icon — one small document, so a catalog or marketplace lists adapters
    /// without downloading or opening a single package.
    /// </summary>
    /// <remarks>
    /// Stored beside the packages rather than among them (see <see cref="AdapterCatalogPaths"/>),
    /// because hosts older than the catalog list every object under the packages' prefix as an
    /// adapter. A deployment that never publishes with the new installer has no catalog, and
    /// everything falls back to reading packages and their metadata as before.
    /// </remarks>
    public class AdapterCatalogEntry
    {
        public const int CurrentCatalogVersion = 1;

        public int CatalogVersion { get; set; } = CurrentCatalogVersion;
        public string Id { get; set; }

        /// <summary>The version that runs when nothing pins one. Null when the current package was uploaded without a version.</summary>
        public string Current { get; set; }

        /// <summary>The manifest of the package that runs now — the current version's, or the unversioned upload's.</summary>
        public AdapterManifest Manifest { get; set; }

        /// <summary>SHA-256 of the package that runs now.</summary>
        public string Sha256 { get; set; }

        /// <summary>The icon as a data: URI, so showing it needs no second read. Kept small by the installer.</summary>
        public string IconDataUri { get; set; }

        public DateTimeOffset UpdatedOn { get; set; }

        /// <summary>Oldest first.</summary>
        public List<AdapterVersionRecord> Versions { get; set; } = new();

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extensions { get; set; }

        public AdapterVersionRecord Find(string version) =>
            Versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

        public static AdapterCatalogEntry Parse(string json) =>
            JsonSerializer.Deserialize<AdapterCatalogEntry>(json, AdapterManifest.JsonOptions)
            ?? throw new JsonException("The adapter catalog entry is empty.");

        public string ToJson() => JsonSerializer.Serialize(this, AdapterManifest.JsonOptions);
    }

    /// <summary>One published version. Never changed once written, apart from <see cref="Withdrawn"/>.</summary>
    public class AdapterVersionRecord
    {
        public string Version { get; set; }
        public string Sha256 { get; set; }
        public DateTimeOffset PublishedOn { get; set; }

        /// <summary>Who published it, as the installer knew them — a user or a CI identity. Informational.</summary>
        public string PublishedBy { get; set; }

        public AdapterManifest Manifest { get; set; }

        /// <summary>Taken out of use: listed for history, not offered for pinning.</summary>
        public bool Withdrawn { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extensions { get; set; }
    }

    /// <summary>
    /// Where things live, relative to the adapters root ("adapters" by default):
    /// <list type="bullet">
    /// <item><c>adapters/{id}</c> — the package that runs when no version is pinned. Unchanged
    /// from before versioning, so every existing host keeps running it.</item>
    /// <item><c>adapters/{id}/{version}</c> — one immutable package per version.</item>
    /// <item><c>adapters-catalog/{id}.json</c> — the catalog entry. Outside <c>adapters/</c> on
    /// purpose: hosts older than the catalog list every key under it as an adapter.</item>
    /// </list>
    /// A pinned version is run by asking for the adapter id <c>{id}/{version}</c>, which every
    /// host already resolves to the versioned package.
    /// </summary>
    public static class AdapterCatalogPaths
    {
        public static string Current(string root, string adapterId) =>
            $"{root}/{adapterId}".ToLowerInvariant();

        public static string Version(string root, string adapterId, string version) =>
            $"{root}/{adapterId}/{version}".ToLowerInvariant();

        public static string CatalogRoot(string root) => $"{root}-catalog/".ToLowerInvariant();

        public static string Catalog(string root, string adapterId) =>
            $"{root}-catalog/{adapterId}.json".ToLowerInvariant();

        /// <summary>The adapter id a host is asked for, to run <paramref name="version"/> — or the current one when empty.</summary>
        public static string Ref(string adapterId, string version) =>
            string.IsNullOrWhiteSpace(version) ? adapterId : $"{adapterId}/{version}";

        /// <summary>Splits a ref made by <see cref="Ref"/> back into id and version (null when unpinned).</summary>
        public static (string AdapterId, string Version) Split(string adapterRef)
        {
            if (string.IsNullOrEmpty(adapterRef)) return (adapterRef, null);
            var slash = adapterRef.LastIndexOf('/');
            if (slash <= 0) return (adapterRef, null);
            var version = adapterRef[(slash + 1)..];
            return IsVersion(version) ? (adapterRef[..slash], version) : (adapterRef, null);
        }

        public static bool IsVersion(string text) =>
            !string.IsNullOrEmpty(text) &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
    }
}
