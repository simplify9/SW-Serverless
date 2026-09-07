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

        public async Task<InstalledAdapter> InstallAsync(string adapterId)
        {
            var metadata = await GetMetadataAsync(adapterId);
            var directory = $"{options.AdapterLocalPath}/{metadata.Hash}";

            await extractionGate.WaitAsync();
            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);

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
                    try
                    {
                        using (var remoteStream = await cloudFilesService.OpenReadAsync(
                                   $"{options.AdapterRemotePath}/{adapterId}".ToLower()))
                        using (var tempFileStream = new FileStream(tempZipPath, FileMode.Create,
                                   FileAccess.Write, FileShare.None))
                        {
                            await remoteStream.CopyToAsync(tempFileStream);
                        }

                        using (var archiveStream = new FileStream(tempZipPath, FileMode.Open,
                                   FileAccess.Read, FileShare.Read))
                        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.Name)) continue;
                                var path = $"{directory}/{entry.FullName.Replace("\\", "/")}";
                                Directory.CreateDirectory(Path.GetDirectoryName(path));
                                entry.ExtractToFile(path, overwrite: true);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        Directory.Delete(directory, true);
                        throw;
                    }
                    finally
                    {
                        try { File.Delete(tempZipPath); } catch { /* best-effort cleanup */ }
                    }
                }
            }
            finally
            {
                extractionGate.Release();
            }

            PruneSupersededVersions(adapterId, metadata.Hash);

            return metadata;
        }

        /// <summary>
        /// Removes older extractions of the same adapter. Without this, every published version
        /// stays on disk for the life of the pod — a few MB each, forever, and worse once pods are
        /// long-lived because adapters are resident.
        /// </summary>
        void PruneSupersededVersions(string adapterId, string keepHash)
        {
            try
            {
                var root = new DirectoryInfo(options.AdapterLocalPath);
                if (!root.Exists) return;

                // Only directories this adapter's entry assembly lives in, so two adapters sharing
                // the local path never delete each other's extractions.
                var entryAssembly = Path.GetFileName(
                    memoryCache.TryGetValue($"{NamingPrefix}.{adapterId}", out InstalledAdapter cached)
                        ? cached.EntryAssembly
                        : null);

                if (string.IsNullOrEmpty(entryAssembly)) return;

                foreach (var directory in root.GetDirectories())
                {
                    if (directory.Name == keepHash) continue;
                    if (!File.Exists(Path.Combine(directory.FullName, entryAssembly))) continue;

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
            installed.LocalPath = Path.GetFullPath(
                $"{options.AdapterLocalPath}/{installed.Hash}/{installed.EntryAssembly}");

            return memoryCache.Set($"{NamingPrefix}.{adapterId}", installed,
                TimeSpan.FromMinutes(options.AdapterMetadataCacheDuration));
        }
    }

    public class InstalledAdapter
    {
        public string AdapterId { get; set; }
        public string Hash { get; set; }
        public string EntryAssembly { get; set; }
        public string LocalPath { get; set; }
        public IDictionary<string, string> AdapterValues { get; set; } = new Dictionary<string, string>();
    }
}
