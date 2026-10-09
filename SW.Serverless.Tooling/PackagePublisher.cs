using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SW.Serverless.Tooling
{
    public class PublishRequest
    {
        public string AdapterId { get; set; }

        /// <summary>The project file: where the author's adapter.json is looked for, and what the language defaults from.</summary>
        public string ProjectPath { get; set; }

        /// <summary>The <c>dotnet publish</c> output. The manifest is written into it before it is zipped.</summary>
        public string PublishPath { get; set; }

        public string EntryAssembly { get; set; }

        /// <summary>Where the zip is built. Beside the publish output, never inside it.</summary>
        public string WorkPath { get; set; }

        /// <summary>major, minor, patch or an explicit version; empty for the unversioned upload.</summary>
        public string Version { get; set; }

        public bool Promote { get; set; } = true;
        public bool Probe { get; set; } = true;
        public TimeSpan? ProbeTimeout { get; set; }
        public string Kind { get; set; }
        public string ReleaseNotes { get; set; }
        public string PublishedBy { get; set; }
    }

    /// <summary>A package sw-serverless build made — in any language — to publish as it is.</summary>
    public class PublishPackageRequest
    {
        public string PackagePath { get; set; }

        /// <summary>Explicit, or major, minor or patch to bump; the manifest's own version when empty.</summary>
        public string Version { get; set; }

        public bool Promote { get; set; } = true;
        public string ReleaseNotes { get; set; }
        public string PublishedBy { get; set; }

        /// <summary>The adapters' folder in storage, as hosts are configured with it; "adapters" unless set.</summary>
        public string RemotePath { get; set; } = AdapterRepository.Root;
    }

    public class PublishResult
    {
        /// <summary>The version published, or null for an unversioned upload.</summary>
        public string Version { get; set; }

        public string Sha256 { get; set; }
        public AdapterManifest Manifest { get; set; }
        public string IconDataUri { get; set; }
    }

    /// <summary>
    /// Everything after the build: manifest, package, upload, catalog. Separate from the build so
    /// it can be run — and tested — against output that is already there.
    /// </summary>
    public static class PackagePublisher
    {
        public static async Task<PublishResult> PublishAsync(ICloudFilesService files, PublishRequest request,
            Action<string> log = null)
        {
            log ??= Console.WriteLine;
            void Warn(string message) => log($"Warning: {message}");

            var adapterId = request.AdapterId?.ToLowerInvariant();
            if (!InstallerLogic.IsValidAdapterId(adapterId))
                throw new SWException(
                    $"Invalid adapter id '{request.AdapterId}'. Use lowercase letters, digits, '.', '_' and '-'.");

            var versioned = !string.IsNullOrWhiteSpace(request.Version);
            if (!versioned && !request.Promote)
                throw new SWException("--no-promote needs -v: an unversioned upload always replaces what runs.");

            // Read from the published assembly rather than asked for: the lifecycle is a fact
            // about the code, and a host that has to be told it separately will eventually be
            // told wrong — which is how a resident adapter ends up offered somewhere only a
            // classic one can run.
            var description = AdapterDescriber.Describe(request.PublishPath, request.EntryAssembly);

            var manifest = ManifestBuilder.Build(ManifestBuilder.LoadAuthorFile(request.ProjectPath),
                new GeneratedManifestFields
                {
                    Id = adapterId,
                    Entry = request.EntryAssembly,
                    Description = description,
                    KindOverride = request.Kind,
                    ReleaseNotes = request.ReleaseNotes,
                    SdkVersion = ManifestBuilder.SdkVersionIn(request.PublishPath, request.EntryAssembly),
                    DefaultLanguage = ManifestBuilder.LanguageOf(request.ProjectPath),
                });

            if (manifest.Properties.Count == 0 && !manifest.IsResident && request.Probe)
            {
                try
                {
                    manifest.Properties = await ExpectedValuesProbe.ProbeAsync(
                        request.PublishPath, request.EntryAssembly, request.ProbeTimeout);
                    log($"Startup values it expects: {(manifest.Properties.Count == 0 ? "none" : string.Join(", ", manifest.Properties.ConvertAll(p => p.Name)))}.");
                }
                catch (Exception ex)
                {
                    // The package is no less runnable for this; its properties are just unknown, as
                    // every adapter's were before manifests.
                    Warn($"Could not ask the adapter which startup values it expects ({ex.Message.Trim()}). " +
                         "Its manifest lists no properties; declare them in adapter.json, or pass --no-probe to skip asking.");
                }
            }

            // Everything that can fail without storage fails before anything is uploaded.
            ManifestBuilder.Validate(manifest);
            var iconDataUri = ManifestBuilder.IconDataUri(manifest, request.PublishPath, Warn);

            var repository = new AdapterRepository(files, log);

            string version = null;
            if (versioned)
            {
                version = await repository.ResolveVersionAsync(adapterId, request.Version);
                manifest.Version = version;
                ManifestBuilder.Validate(manifest);
                log($"Publishing version {version} of '{adapterId}'.");
            }

            ManifestBuilder.WriteInto(manifest, request.PublishPath);

            Directory.CreateDirectory(request.WorkPath);
            var zipPath = Path.Combine(request.WorkPath, "adapter.zip");
            if (!new InstallerLogic().Compress(request.PublishPath, zipPath))
                throw new SWException("The package could not be built, so nothing was uploaded.");

            var sha256 = InstallerLogic.Sha256Of(zipPath);
            var package = new PackageInfo
            {
                EntryAssembly = request.EntryAssembly,
                Lifecycle = manifest.Lifecycle,
                // The metadata says what the manifest says, so a host reading either agrees.
                Kind = string.Join(",", manifest.Kinds),
                Manifest = manifest,
                IconDataUri = iconDataUri,
            };

            log($"Lifecycle: {package.Lifecycle}"
                + (string.IsNullOrEmpty(package.Kind) ? "" : $", kind: {package.Kind}")
                + $", sha256: {sha256}");

            if (versioned)
                await repository.PublishVersionAsync(adapterId, version, zipPath, package, request.Promote,
                    request.PublishedBy);
            else
                await repository.PublishUnversionedAsync(adapterId, zipPath, package);

            return new PublishResult { Version = version, Sha256 = sha256, Manifest = manifest, IconDataUri = iconDataUri };
        }

        /// <summary>
        /// Publishes a package sw-serverless build made: its manifest is already complete, so this only
        /// settles the version, stamps it into the manifest and uploads. Always versioned; an adapter
        /// in another runtime goes only where hosts that can run it look.
        /// </summary>
        public static async Task<PublishResult> PublishPackageAsync(ICloudFilesService files, PublishPackageRequest request,
            Action<string> log = null)
        {
            log ??= Console.WriteLine;
            void Warn(string message) => log($"Warning: {message}");

            if (!File.Exists(request.PackagePath))
                throw new SWException($"There is no package at {request.PackagePath}.");

            var work = Path.Combine(Path.GetTempPath(), "swsl-publish", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var folder = Path.Combine(work, "package");
                System.IO.Compression.ZipFile.ExtractToDirectory(request.PackagePath, folder);
                var manifestPath = Path.Combine(folder, AdapterManifest.FileName);
                if (!File.Exists(manifestPath))
                    throw new SWException($"{request.PackagePath} has no {AdapterManifest.FileName}; build it with sw-serverless build.");

                var manifest = AdapterManifest.Parse(await File.ReadAllTextAsync(manifestPath));
                var adapterId = manifest.Id?.ToLowerInvariant();
                if (!InstallerLogic.IsValidAdapterId(adapterId))
                    throw new SWException($"The package's manifest has no valid id ('{manifest.Id}').");

                var mode = string.IsNullOrWhiteSpace(request.Version) ? manifest.Version : request.Version;
                if (string.IsNullOrWhiteSpace(mode))
                    throw new SWException("The package has no version: give one with -v, or set version in adapter.json.");

                var repository = new AdapterRepository(files, log, request.RemotePath);
                var version = await repository.ResolveVersionAsync(adapterId, mode);
                manifest.Version = version;
                manifest.PublishedOn = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(request.ReleaseNotes)) manifest.ReleaseNotes = request.ReleaseNotes;
                ManifestBuilder.Validate(manifest);
                await File.WriteAllTextAsync(manifestPath, manifest.ToJson());

                var zipPath = Path.Combine(work, "adapter.zip");
                if (!new InstallerLogic().Compress(folder, zipPath))
                    throw new SWException("The package could not be rebuilt with its version, so nothing was uploaded.");

                var iconDataUri = ManifestBuilder.IconDataUri(manifest, folder, Warn);
                var package = new PackageInfo
                {
                    EntryAssembly = manifest.Entry,
                    Lifecycle = manifest.Lifecycle,
                    Kind = string.Join(",", manifest.Kinds),
                    Manifest = manifest,
                    IconDataUri = iconDataUri,
                };

                log($"Publishing version {version} of '{adapterId}' ({manifest.Runtime}).");
                await repository.PublishVersionAsync(adapterId, version, zipPath, package, request.Promote, request.PublishedBy);
                return new PublishResult { Version = version, Sha256 = InstallerLogic.Sha256Of(zipPath), Manifest = manifest, IconDataUri = iconDataUri };
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }
    }
}
