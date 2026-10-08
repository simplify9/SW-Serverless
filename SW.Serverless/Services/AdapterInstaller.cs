using Microsoft.Extensions.Caching.Memory;
using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless
{
    /// <summary>
    /// Resolves an adapter id to a local entry assembly, downloading and extracting it from cloud
    /// storage on first use. Shared by the classic per-invocation path and the resident one, so
    /// "install an adapter at runtime" means the same thing for both.
    /// </summary>
    public class AdapterInstaller
    {
        public const string NamingPrefix = "serverless.adapters";

        static readonly SemaphoreSlim extractionGate = new(1, 1);

        readonly ServerlessOptions options;
        readonly IMemoryCache memoryCache;
        readonly ICloudFilesService cloudFilesService;

        public AdapterInstaller(ServerlessOptions options, IMemoryCache memoryCache,
            ICloudFilesService cloudFilesService)
        {
            this.options = options;
            this.memoryCache = memoryCache;
            this.cloudFilesService = cloudFilesService;
        }

        /// <summary>
        /// The most an adapter package may unpack to. A guard against a zip bomb filling the node's
        /// disk, set well above any real adapter (the largest seen are a few hundred MB).
        /// </summary>
        public static long MaxExtractedBytes { get; set; } = 4L * 1024 * 1024 * 1024;

        /// <summary>The file in each extraction that names the adapter it belongs to.</summary>
        internal const string OwnerMarker = ".swsl-adapter";

        public async Task<InstalledAdapter> InstallAsync(string adapterId)
        {
            var metadata = await GetMetadataAsync(adapterId);
            var directory = Path.GetFullPath(Path.Combine(options.AdapterLocalPath, DirectoryNameOf(metadata.Hash)));

            await extractionGate.WaitAsync();
            try
            {
                if (!Directory.Exists(directory))
                    await ExtractAsync(adapterId, directory);
            }
            finally
            {
                extractionGate.Release();
            }

            PruneSupersededVersions(adapterId, directory);

            return metadata;
        }

        /// <summary>
        /// Downloads and unpacks into a temporary sibling directory, then renames it into place. An
        /// extraction directory therefore only ever exists complete: a host killed half-way leaves a
        /// temporary directory behind rather than a half-filled one that "exists, so installed" would
        /// trust forever, and a second host process sharing the path sees either nothing or all of it.
        /// </summary>
        async Task ExtractAsync(string adapterId, string directory)
        {
            // ZipArchive needs a SEEKABLE stream to read the central directory. The
            // cloud-storage read stream is not seekable, so handing it directly to
            // ZipArchive silently makes .NET buffer the entire archive into one
            // in-memory MemoryStream first - for a large adapter (DevExpress-sized,
            // several hundred MB uncompressed) that one-time spike is big enough to
            // OOM the whole host process on a memory-constrained container, well
            // before the child adapter process itself even starts. Downloading to a
            // temp FILE first keeps memory use to one bounded copy-buffer regardless
            // of archive size, and a FileStream is seekable so ZipArchive reads
            // straight off disk.
            var tempZipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
            var staging = $"{directory}.extracting-{Guid.NewGuid():N}";
            try
            {
                using (var remoteStream = await cloudFilesService.OpenReadAsync(
                           $"{options.AdapterRemotePath}/{adapterId}".ToLower()))
                using (var tempFileStream = new FileStream(tempZipPath, FileMode.Create,
                           FileAccess.Write, FileShare.None))
                {
                    await remoteStream.CopyToAsync(tempFileStream);
                }

                Directory.CreateDirectory(staging);
                using (var archiveStream = new FileStream(tempZipPath, FileMode.Open,
                           FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
                {
                    long total = 0;
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        // An entry's name is the package author's to choose, and "../../app/x.dll"
                        // would otherwise be written over the host's own files before any adapter
                        // code ran. Every entry must land inside the extraction directory.
                        var path = ContainedPath(staging, entry.FullName.Replace("\\", "/"))
                                   ?? throw new InvalidDataException(
                                       $"Adapter '{adapterId}' package has an entry outside its directory: '{entry.FullName}'.");

                        total += entry.Length;
                        if (total > MaxExtractedBytes)
                            throw new InvalidDataException(
                                $"Adapter '{adapterId}' package unpacks to more than {MaxExtractedBytes} bytes.");

                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        entry.ExtractToFile(path, overwrite: true);
                    }
                }

                await File.WriteAllTextAsync(Path.Combine(staging, OwnerMarker), adapterId.ToLowerInvariant());

                try
                {
                    Directory.Move(staging, directory);
                }
                catch (IOException) when (Directory.Exists(directory))
                {
                    // Another host process sharing the path finished the same version first.
                    Directory.Delete(staging, true);
                }
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best effort */ }
                throw;
            }
            finally
            {
                try { File.Delete(tempZipPath); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// <paramref name="relative"/> under <paramref name="root"/>, or null when it would resolve
        /// outside it — an absolute path, or one climbing out with "..".
        /// </summary>
        internal static string ContainedPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
            return full.StartsWith(fullRoot, StringComparison.Ordinal) ? full : null;
        }

        /// <summary>
        /// The extraction directory's name for a package <paramref name="hash"/>. The hash comes from
        /// storage metadata — an ETag, usually — and is used as a path segment, so anything beyond
        /// letters, digits, '.', '_' and '-' (quotes in an ETag, or a crafted "../..") is replaced
        /// by a digest of it: the same hash still always maps to the same directory.
        /// </summary>
        internal static string DirectoryNameOf(string hash)
        {
            var plain = hash.Length <= 128 && hash.Trim('.').Length > 0 &&
                        System.Linq.Enumerable.All(hash, c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
            if (plain) return hash;
            return "h" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(hash)))[..32].ToLowerInvariant();
        }

        /// <summary>
        /// Removes older extractions of the same adapter. Without this, every published version
        /// stays on disk for the life of the pod — a few MB each, forever, and worse once pods are
        /// long-lived because adapters are resident.
        /// </summary>
        /// <remarks>
        /// Only directories stamped with this adapter's id are candidates — matching by entry
        /// assembly name let two adapters both built as "Adapter.dll" delete each other — and a
        /// directory a running adapter was started from is kept: on Linux the delete succeeds under
        /// a live process, which then fails the next time it loads an assembly, and a restart of it
        /// finds nothing to start.
        /// </remarks>
        void PruneSupersededVersions(string adapterId, string keepDirectory)
        {
            try
            {
                var root = new DirectoryInfo(options.AdapterLocalPath);
                if (!root.Exists) return;

                foreach (var directory in root.GetDirectories())
                {
                    if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar),
                            keepDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)) continue;

                    if (directory.Name.Contains(".extracting-", StringComparison.Ordinal)) continue;
                    var marker = Path.Combine(directory.FullName, OwnerMarker);
                    if (!File.Exists(marker)) continue;
                    if (!string.Equals(File.ReadAllText(marker).Trim(), adapterId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (AdapterDirectoryLeases.InUse(directory.FullName)) continue;

                    try { directory.Delete(recursive: true); }
                    catch { /* still in use by a running adapter; the next install retries */ }
                }
            }
            catch { /* pruning is housekeeping and must never fail an install */ }
        }

        public async Task<InstalledAdapter> GetMetadataAsync(string adapterId)
        {
            if (memoryCache.TryGetValue($"{NamingPrefix}.{adapterId}", out InstalledAdapter cached))
                return cached;

            if (cloudFilesService == null)
                throw new InvalidOperationException(
                    $"Adapter '{adapterId}' must be installed from cloud storage, but no " +
                    "ICloudFilesService is registered. Register one, or start it from a local path.");

            var remotePath = $"{options.AdapterRemotePath}/{adapterId}".ToLower();
            var raw = await cloudFilesService.GetMetadataAsync(remotePath);
            var metadata = new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);

            if (!metadata.TryGetValue("EntryAssembly", out var entryAssembly) ||
                string.IsNullOrWhiteSpace(entryAssembly))
                throw new InvalidOperationException(
                    $"Adapter '{adapterId}' metadata at '{remotePath}' is missing 'EntryAssembly'.");

            if (!metadata.TryGetValue("Hash", out var hash) || string.IsNullOrWhiteSpace(hash))
                throw new InvalidOperationException(
                    $"Adapter '{adapterId}' metadata at '{remotePath}' is missing 'Hash'.");

            var installed = new InstalledAdapter
            {
                AdapterId = adapterId,
                EntryAssembly = entryAssembly,
                Hash = hash,
                AdapterValues = metadata
            };
            // The entry assembly is a path inside the package, never one out of it.
            installed.Directory = Path.GetFullPath(Path.Combine(options.AdapterLocalPath, DirectoryNameOf(installed.Hash)));
            installed.LocalPath = ContainedPath(installed.Directory, installed.EntryAssembly.Replace("\\", "/"))
                                  ?? throw new InvalidOperationException(
                                      $"Adapter '{adapterId}' metadata at '{remotePath}' has an 'EntryAssembly' outside its package.");

            return memoryCache.Set($"{NamingPrefix}.{adapterId}", installed,
                TimeSpan.FromMinutes(options.AdapterMetadataCacheDuration));
        }
    }

    /// <summary>
    /// The extraction directories running adapters were started from, so pruning an older version
    /// never deletes one out from under a live process. Counted, because several instances of one
    /// adapter version share a directory.
    /// </summary>
    public static class AdapterDirectoryLeases
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> leases = new(StringComparer.Ordinal);

        static string Normalize(string directory) =>
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);

        /// <summary>Holds <paramref name="directory"/> until the returned handle is disposed.</summary>
        public static IDisposable Hold(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return Released.Instance;
            var key = Normalize(directory);
            leases.AddOrUpdate(key, 1, (_, n) => n + 1);
            return new Lease(key);
        }

        public static bool InUse(string directory) =>
            leases.TryGetValue(Normalize(directory), out var n) && n > 0;

        sealed class Lease(string key) : IDisposable
        {
            int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 1) return;
                var remaining = leases.AddOrUpdate(key, 0, (_, n) => n - 1);
                if (remaining <= 0) leases.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(key, remaining));
            }
        }

        sealed class Released : IDisposable
        {
            public static readonly Released Instance = new();
            public void Dispose() { }
        }
    }

    public class InstalledAdapter
    {
        public string AdapterId { get; set; }
        public string Hash { get; set; }
        public string EntryAssembly { get; set; }
        public string LocalPath { get; set; }

        /// <summary>The extraction directory the package was unpacked into.</summary>
        public string Directory { get; set; }

        public IDictionary<string, string> AdapterValues { get; set; } = new Dictionary<string, string>();
    }
}
