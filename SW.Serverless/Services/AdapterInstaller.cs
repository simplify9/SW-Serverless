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
                    try
                    {
                        using var stream = await cloudFilesService.OpenReadAsync(
                            $"{options.AdapterRemotePath}/{adapterId}".ToLower());
                        using var archive = new ZipArchive(stream);

                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;
                            var path = $"{directory}/{entry.FullName.Replace("\\", "/")}";
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            entry.ExtractToFile(path, overwrite: true);
                        }
                    }
                    catch (Exception)
                    {
                        Directory.Delete(directory, true);
                        throw;
                    }
                }
            }
            finally
            {
                extractionGate.Release();
            }

            return metadata;
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
