using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.Tooling
{
    /// <summary>What goes into storage for one package, beside the zip itself.</summary>
    public class PackageInfo
    {
        public string EntryAssembly { get; set; }
        public string Lifecycle { get; set; } = AdapterDescription.ClassicLifecycle;

        /// <summary>Comma separated, as the Kind metadata has always been.</summary>
        public string Kind { get; set; } = "";

        public AdapterManifest Manifest { get; set; }
        public string IconDataUri { get; set; }
    }

    /// <summary>One row of <c>sw-serverless versions</c>.</summary>
    public class VersionRow
    {
        public string Version { get; set; }
        public DateTimeOffset? PublishedOn { get; set; }
        public string PublishedBy { get; set; }
        public string Sha256 { get; set; }
        public bool Withdrawn { get; set; }
        public bool Current { get; set; }
    }

    public class VersionListing
    {
        /// <summary>False when the adapter has no catalog entry and the rows were read from the packages.</summary>
        public bool FromCatalog { get; set; }

        public string Current { get; set; }
        public List<VersionRow> Versions { get; set; } = new();
    }

    /// <summary>
    /// The adapters layout in storage, and the only code in the installer that writes to it.
    ///
    /// The layout is a contract with production deployments running hosts — and applications that
    /// list adapters from storage — older than the catalog, so it is held to three rules:
    /// <list type="number">
    /// <item><c>adapters/{id}</c> always holds whatever is current, with the full metadata an old
    /// host needs to run it (EntryAssembly and Hash at least).</item>
    /// <item>Nothing but <c>adapters/{id}</c> is ever written under <c>adapters/</c> — older
    /// listings take every key there for an adapter. Versions an older installer put at
    /// <c>adapters/{id}/{version}</c> are read, never written.</item>
    /// <item>Everything new lives beside it: versions under <c>adapters-versions/</c>, the catalog
    /// under <c>adapters-catalog/</c>. Nothing old reads either.</item>
    /// </list>
    /// </summary>
    public class AdapterRepository
    {
        public const string Root = "adapters";

        /// <summary>Metadata a provider adds itself on read, and so is never copied from one object to another.</summary>
        static readonly HashSet<string> ProviderMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            "ContentType", "ContentLength", "LastModified", "ETag", "Hash",
        };

        readonly ICloudFilesService files;
        readonly AdapterCatalogStore catalog;
        readonly Action<string> log;
        readonly string root;

        /// <param name="root">
        /// The adapters' folder in storage, as hosts are configured with it (AdapterRemotePath);
        /// <see cref="Root"/> unless a deployment changed it.
        /// </param>
        public AdapterRepository(ICloudFilesService files, Action<string> log = null, string root = Root)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.root = string.IsNullOrWhiteSpace(root) ? Root : root.Trim().TrimEnd('/');
            catalog = new AdapterCatalogStore(files, this.root);
            this.log = log ?? Console.WriteLine;
        }

        /// <summary>The folder this repository publishes under.</summary>
        public string RemotePath => root;

        public AdapterCatalogStore Catalog => catalog;

        // ---------------------------------------------------------------- metadata

        /// <summary>
        /// What every package carries. Kind and Lifecycle are written even when empty so a host can
        /// tell "declared nothing" from "predates the field". Sha256 is the hex digest of the zip.
        /// </summary>
        /// <remarks>
        /// Hash is the same digest. Hosts require it and name the extraction directory after it; S3,
        /// Azure and Oracle supply one from the ETag on read, but the Google and filesystem
        /// providers do not, so a package published to either could not be installed by any host.
        /// S3 ignores a written Hash in favour of its ETag, so nothing changes there; elsewhere the
        /// content digest is a better directory name than an ETag — the same bytes, the same folder.
        /// Version is empty for an unversioned upload.
        /// </remarks>
        public static Dictionary<string, string> LegacyMetadata(
            string entryAssembly, string lifecycle, string kind, string sha256, string version) => new()
        {
            { "EntryAssembly", entryAssembly },
            { "Lang", "dotnet" },
            { "Timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            { "Lifecycle", string.IsNullOrWhiteSpace(lifecycle) ? AdapterDescription.ClassicLifecycle : lifecycle },
            { "Kind", kind ?? "" },
            { "Sha256", sha256 },
            { "Hash", sha256 },
            { "Version", version ?? "" },
        };

        // ---------------------------------------------------------------- versions

        public static string CurrentKey(string adapterId) => AdapterCatalogPaths.Current(Root, adapterId);

        string CurrentKeyOf(string adapterId) => AdapterCatalogPaths.Current(root, adapterId);
        string VersionKeyOf(string adapterId, string version) => AdapterCatalogPaths.Version(root, adapterId, version);

        /// <summary>Where this installer writes a version: <c>adapters-versions/{id}/{version}</c>.</summary>
        public static string VersionKey(string adapterId, string version) => AdapterCatalogPaths.Version(Root, adapterId, version);

        /// <summary>
        /// Every published version and the key its package is at: <c>adapters-versions/{id}/</c>,
        /// and <c>adapters/{id}/</c> where an installer from before that layout put them. Only ever
        /// read there — nothing new is written under <c>adapters/</c> but the current package.
        /// </summary>
        public async Task<Dictionary<string, string>> LocateVersionsAsync(string adapterId)
        {
            var located = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var versionsRoot = AdapterCatalogPaths.VersionsRoot(root, adapterId).TrimEnd('/');
            foreach (var version in InstallerLogic.ExistingVersions(versionsRoot,
                         (await files.ListAsync($"{versionsRoot}/")).Select(f => f.Key)))
                if (AdapterCatalogPaths.IsVersion(version)) located[version] = VersionKeyOf(adapterId, version);

            // Listed WITH the slash, so "foo" does not pick up "foobar"'s versions, nor the current
            // package adapters/foo itself.
            var legacy = CurrentKeyOf(adapterId);
            foreach (var version in InstallerLogic.ExistingVersions(legacy,
                         (await files.ListAsync($"{legacy}/")).Select(f => f.Key)))
                if (AdapterCatalogPaths.IsVersion(version))
                    located.TryAdd(version, AdapterCatalogPaths.LegacyVersion(root, adapterId, version));

            return located;
        }

        /// <summary>The version names published for the adapter, whatever the catalog says.</summary>
        public async Task<List<string>> PublishedVersionsAsync(string adapterId) =>
            (await LocateVersionsAsync(adapterId)).Keys.ToList();

        public async Task<bool> ExistsAsync(string key) =>
            // Listed rather than read: what a missing key throws differs by provider.
            (await files.ListAsync(key)).Any(f => string.Equals(f.Key, key, StringComparison.Ordinal));

        /// <summary>
        /// The version to publish for <paramref name="mode"/> — major, minor, patch, or explicit.
        /// Counts both the packages and the catalog, so a version whose package is missing is still
        /// never reused.
        /// </summary>
        public async Task<string> ResolveVersionAsync(string adapterId, string mode)
        {
            var existing = await PublishedVersionsAsync(adapterId);
            var entry = await catalog.GetAsync(adapterId);
            if (entry != null)
                existing.AddRange(entry.Versions.Select(v => v.Version).Where(v => !existing.Contains(v)));

            return Semver.GetNewVersion(mode, existing);
        }

        // ---------------------------------------------------------------- catalog entry

        /// <summary>
        /// The adapter's catalog entry, created when it has none, and with a record added for every
        /// version package the catalog does not know — an adapter published by an older installer,
        /// or by one that ran alongside this one. Not saved: the caller saves once it has changed it.
        /// </summary>
        public async Task<AdapterCatalogEntry> LoadEntryAsync(string adapterId)
        {
            var entry = await catalog.GetAsync(adapterId) ?? new AdapterCatalogEntry { Id = adapterId };
            entry.Versions ??= new List<AdapterVersionRecord>();

            var located = await LocateVersionsAsync(adapterId);
            var missing = located.Keys.Where(v => entry.Find(v) == null).ToList();
            if (missing.Count == 0) return entry;

            foreach (var version in missing)
            {
                var metadata = await MetadataAsync(located[version]);
                entry.Versions.Add(new AdapterVersionRecord
                {
                    Version = version,
                    Sha256 = Value(metadata, "Sha256"),
                    PublishedOn = ParseTimestamp(Value(metadata, "Timestamp")) ?? DateTimeOffset.MinValue,
                });
            }

            // Oldest first, as the model promises. Stable, so records already in order stay so.
            entry.Versions = entry.Versions.OrderBy(v => v.PublishedOn).ToList();
            return entry;
        }

        // ---------------------------------------------------------------- publish

        /// <summary>
        /// Uploads a versioned package to <c>adapters-versions/{id}/{version}</c>, and — when
        /// <paramref name="promote"/> — the same package to <c>adapters/{id}</c>, then records it
        /// in the catalog.
        /// </summary>
        public async Task PublishVersionAsync(string adapterId, string version, string zipPath, PackageInfo package,
            bool promote, string publishedBy)
        {
            var versionKey = VersionKeyOf(adapterId, version);

            // Immutable: Semver already refuses an explicit version that exists, and this catches
            // one published between resolving the number and uploading it.
            if ((await LocateVersionsAsync(adapterId)).ContainsKey(version))
                throw new SWException($"Version {version} of '{adapterId}' has already been published.");

            // An id keeps its runtime: hosts that predate manifests run adapters/{id} with dotnet,
            // and would go on running the old .NET package under an id that had moved on.
            var runsOnDotnet = RunsOnDotnet(package.Manifest);
            if (!runsOnDotnet && await ExistsAsync(CurrentKeyOf(adapterId)))
                throw new SWException(
                    $"'{adapterId}' is a .NET adapter, and older hosts would keep running that under its id. " +
                    $"Publish the {package.Manifest!.Runtime} adapter under a new id.");

            // Loaded before uploading, so the new version is not mistaken for one the catalog missed.
            var entry = await LoadEntryAsync(adapterId);
            var sha256 = InstallerLogic.Sha256Of(zipPath);
            var metadata = LegacyMetadata(package.EntryAssembly, package.Lifecycle, package.Kind, sha256, version);

            await UploadAsync(versionKey, zipPath, metadata);
            // adapters/{id} is what hosts before manifests list and run, always with dotnet. An
            // adapter in another runtime never goes there; newer hosts find its current version in
            // the catalog.
            if (promote && runsOnDotnet) await UploadAsync(CurrentKeyOf(adapterId), zipPath, metadata);

            entry.Versions.Add(new AdapterVersionRecord
            {
                Version = version,
                Sha256 = sha256,
                PublishedOn = package.Manifest?.PublishedOn ?? DateTimeOffset.UtcNow,
                PublishedBy = publishedBy,
                Manifest = package.Manifest,
            });

            if (promote) MakeCurrent(entry, version, package.Manifest, sha256, package.IconDataUri);

            await catalog.SaveAsync(entry);
            log(promote
                ? $"Version {version} of '{adapterId}' is published and current."
                : $"Version {version} of '{adapterId}' is published; '{adapterId}' still runs " +
                  $"{(entry.Current ?? "its unversioned package")}. Run 'sw-serverless promote {adapterId} {version}' to switch.");
        }

        /// <summary>
        /// The upload every installer has always made: <c>adapters/{id}</c> and nothing else under
        /// <c>adapters/</c>. The catalog follows it, with no current version, so a catalog never shows
        /// a version as running when an unversioned package has replaced it. History is untouched.
        /// </summary>
        public async Task PublishUnversionedAsync(string adapterId, string zipPath, PackageInfo package)
        {
            // An unversioned package lives only at adapters/{id}, where older hosts would start it
            // with dotnet whatever it is written in.
            if (!RunsOnDotnet(package.Manifest))
                throw new SWException(
                    $"A {package.Manifest!.Runtime} adapter must be published with a version (-v), so older hosts never see it.");

            var entry = await LoadEntryAsync(adapterId);
            var sha256 = InstallerLogic.Sha256Of(zipPath);

            await UploadAsync(CurrentKeyOf(adapterId), zipPath,
                LegacyMetadata(package.EntryAssembly, package.Lifecycle, package.Kind, sha256, null));

            MakeCurrent(entry, null, package.Manifest, sha256, package.IconDataUri);
            await catalog.SaveAsync(entry);
        }

        /// <summary>A package with no manifest, or no runtime in it, is .NET — as every package before manifests was.</summary>
        static bool RunsOnDotnet(AdapterManifest manifest) =>
            string.IsNullOrWhiteSpace(manifest?.Runtime) ||
            string.Equals(manifest.Runtime, AdapterManifest.DotnetRuntime, StringComparison.OrdinalIgnoreCase);

        static void MakeCurrent(AdapterCatalogEntry entry, string version, AdapterManifest manifest, string sha256,
            string iconDataUri)
        {
            entry.Current = version;
            entry.Manifest = manifest;
            entry.Sha256 = sha256;
            entry.IconDataUri = iconDataUri;
        }

        // ---------------------------------------------------------------- promote

        /// <summary>
        /// Makes a published version the one that runs: copies its package over
        /// <c>adapters/{id}</c> with the full metadata an old host needs, and points the catalog at
        /// it. Rolling back is promoting an older version. Works for versions published before the
        /// catalog existed, reading what it needs from the package and its metadata.
        /// </summary>
        public async Task PromoteAsync(string adapterId, string version, string workDirectory)
        {
            if (!AdapterCatalogPaths.IsVersion(version))
                throw new SWException($"'{version}' is not a version such as 1.4.0.");

            if (!(await LocateVersionsAsync(adapterId)).TryGetValue(version, out var versionKey))
                throw new SWException($"'{adapterId}' has no published version {version}.");

            var entry = await LoadEntryAsync(adapterId);
            var record = entry.Find(version)
                         ?? throw new SWException($"'{adapterId}' has no published version {version}.");
            if (record.Withdrawn)
                throw new SWException($"Version {version} of '{adapterId}' is withdrawn and cannot be made current.");

            Directory.CreateDirectory(workDirectory);
            var zipPath = Path.Combine(workDirectory, $"{Guid.NewGuid():N}.zip");
            try
            {
                await using (var remote = await files.OpenReadAsync(versionKey))
                await using (var local = File.Create(zipPath))
                    await remote.CopyToAsync(local);

                var sha256 = InstallerLogic.Sha256Of(zipPath);
                var versionMetadata = await MetadataAsync(versionKey);

                // The catalog's digest was taken at publish time, so a package changed since — or
                // damaged in transit — is caught here rather than run.
                var expected = record.Sha256 ?? Value(versionMetadata, "Sha256");
                if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SWException(
                        $"Version {version} of '{adapterId}' does not match the digest recorded when it was published " +
                        $"({Short(expected)} recorded, {Short(sha256)} in storage). Nothing was changed.");

                var manifest = ReadManifest(zipPath) ?? record.Manifest;

                // Another runtime is never copied to adapters/{id}, where hosts before manifests
                // would start it with dotnet: being current is the catalog's alone.
                if (!RunsOnDotnet(manifest))
                {
                    record.Sha256 ??= sha256;
                    record.Manifest ??= manifest;
                    MakeCurrent(entry, version, manifest, sha256, IconFromPackage(zipPath, manifest));
                    await catalog.SaveAsync(entry);
                    log($"Version {version} of '{adapterId}' is now current.");
                    return;
                }

                var entryAssembly = Value(versionMetadata, "EntryAssembly") ?? manifest?.Entry;
                if (string.IsNullOrWhiteSpace(entryAssembly))
                    throw new SWException(
                        $"Version {version} of '{adapterId}' does not say which assembly to start, so an older host could not run it.");

                var lifecycle = Value(versionMetadata, "Lifecycle") ?? manifest?.Lifecycle;
                var kind = Value(versionMetadata, "Kind") ?? string.Join(",", manifest?.Kinds ?? new List<string>());

                // Anything else the version carried (a Protocol, a Launcher…) goes with it: a host
                // reads those from the object it runs, and that is now adapters/{id}.
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in versionMetadata.Where(m => !ProviderMetadata.Contains(m.Key)))
                    metadata[item.Key] = item.Value;
                foreach (var item in LegacyMetadata(entryAssembly, lifecycle, kind, sha256, version))
                    metadata[item.Key] = item.Value;

                await UploadAsync(CurrentKeyOf(adapterId), zipPath, metadata);

                record.Sha256 ??= sha256;
                record.Manifest ??= manifest;
                MakeCurrent(entry, version, manifest, sha256, IconFromPackage(zipPath, manifest));
                await catalog.SaveAsync(entry);

                log($"Version {version} of '{adapterId}' is now current.");
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
        }

        // ---------------------------------------------------------------- withdraw

        /// <summary>
        /// Takes a version out of use: still listed for history, never offered for pinning, and
        /// refused by promote. The package stays, because a deployment may still pin it.
        /// </summary>
        public async Task WithdrawAsync(string adapterId, string version)
        {
            var entry = await LoadEntryAsync(adapterId);
            var record = entry.Find(version)
                         ?? throw new SWException($"'{adapterId}' has no published version {version}.");

            if (string.Equals(entry.Current, version, StringComparison.OrdinalIgnoreCase))
                throw new SWException(
                    $"Version {version} is the current version of '{adapterId}'. Promote another version first.");

            if (record.Withdrawn)
            {
                log($"Version {version} of '{adapterId}' was already withdrawn.");
                return;
            }

            record.Withdrawn = true;
            await catalog.SaveAsync(entry);
            log($"Version {version} of '{adapterId}' is withdrawn.");
        }

        // ---------------------------------------------------------------- list

        /// <summary>
        /// Every version, from the catalog — or, for an adapter published before it, from the
        /// packages themselves, with the current one recognised by its Version or Sha256 metadata.
        /// </summary>
        public async Task<VersionListing> ListVersionsAsync(string adapterId)
        {
            var entry = await catalog.GetAsync(adapterId);
            if (entry != null)
            {
                return new VersionListing
                {
                    FromCatalog = true,
                    Current = entry.Current,
                    Versions = entry.Versions.Select(v => new VersionRow
                    {
                        Version = v.Version,
                        PublishedOn = v.PublishedOn == DateTimeOffset.MinValue ? null : v.PublishedOn,
                        PublishedBy = v.PublishedBy,
                        Sha256 = v.Sha256,
                        Withdrawn = v.Withdrawn,
                        Current = string.Equals(v.Version, entry.Current, StringComparison.OrdinalIgnoreCase),
                    }).ToList(),
                };
            }

            var listing = new VersionListing { FromCatalog = false };
            var current = await ExistsAsync(CurrentKeyOf(adapterId)) ? await MetadataAsync(CurrentKeyOf(adapterId)) : null;
            var currentVersion = Value(current, "Version");
            var currentSha = Value(current, "Sha256");

            foreach (var (version, key) in await LocateVersionsAsync(adapterId))
            {
                var metadata = await MetadataAsync(key);
                var sha256 = Value(metadata, "Sha256");
                listing.Versions.Add(new VersionRow
                {
                    Version = version,
                    PublishedOn = ParseTimestamp(Value(metadata, "Timestamp")),
                    Sha256 = sha256,
                    Current = currentVersion != null
                        ? string.Equals(currentVersion, version, StringComparison.OrdinalIgnoreCase)
                        : currentSha != null && string.Equals(currentSha, sha256, StringComparison.OrdinalIgnoreCase),
                });
            }

            listing.Versions = listing.Versions.OrderBy(v => v.PublishedOn ?? DateTimeOffset.MinValue).ToList();
            listing.Current = listing.Versions.FirstOrDefault(v => v.Current)?.Version;
            return listing;
        }

        // ---------------------------------------------------------------- helpers

        async Task UploadAsync(string key, string zipPath, Dictionary<string, string> metadata)
        {
            log($"Uploading to {key}");
            await using var stream = File.OpenRead(zipPath);
            try
            {
                await files.WriteAsync(stream, new WriteFileSettings
                {
                    ContentType = "application/zip",
                    Key = key,
                    Metadata = metadata,
                });
            }
            catch (IOException ex)
            {
                // The filesystem provider maps a key to a file, so "adapters/foo" cannot be written
                // where an older installer left versions in a folder "adapters/foo/".
                throw new SWException(
                    $"Could not write {key}: {ex.Message} On the filesystem ('local') provider an adapter's current " +
                    "package cannot sit beside versions an older installer put under adapters/{id}/; move those to " +
                    "adapters-versions/{id}/.");
            }
        }

        async Task<IReadOnlyDictionary<string, string>> MetadataAsync(string key)
        {
            var raw = await files.GetMetadataAsync(key);
            return raw == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                // Case-insensitively: S3 hands metadata names back lower-cased.
                : new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
        }

        static string Value(IReadOnlyDictionary<string, string> metadata, string name) =>
            metadata != null && metadata.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        static DateTimeOffset? ParseTimestamp(string text) =>
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value
                : null;

        public static string Short(string sha256) =>
            string.IsNullOrEmpty(sha256) ? "" : sha256.Length > 12 ? sha256[..12] : sha256;

        /// <summary>The adapter.json inside a package, or null for one built before manifests.</summary>
        public static AdapterManifest ReadManifest(string zipPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry(AdapterManifest.FileName);
                if (entry == null) return null;
                using var reader = new StreamReader(entry.Open());
                return AdapterManifest.Parse(reader.ReadToEnd());
            }
            catch
            {
                // A package that runs but carries an unreadable manifest is still promotable.
                return null;
            }
        }

        string IconFromPackage(string zipPath, AdapterManifest manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest?.Icon)) return null;
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var name = manifest.Icon.Replace('\\', '/');
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.Ordinal));
                if (entry == null) return null;

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return ManifestBuilder.DataUri(manifest.Icon, buffer.ToArray(), log);
            }
            catch
            {
                return null;
            }
        }
    }
}
