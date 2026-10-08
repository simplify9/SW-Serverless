using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SW.Serverless.Installer.Shared
{
    /// <summary>What the installer knows about a build, and fills into the manifest over whatever the author wrote.</summary>
    public class GeneratedManifestFields
    {
        public string Id { get; set; }

        /// <summary>The version being published; null for an unversioned upload.</summary>
        public string Version { get; set; }

        public string Entry { get; set; }
        public AdapterDescription Description { get; set; }

        /// <summary>--kind, comma separated. Wins over everything, as it always has.</summary>
        public string KindOverride { get; set; }

        /// <summary>--notes. Wins over the author's releaseNotes.</summary>
        public string ReleaseNotes { get; set; }

        public string SdkVersion { get; set; }

        /// <summary>The language when the author did not say: from the project file's extension.</summary>
        public string DefaultLanguage { get; set; } = "csharp";

        public DateTimeOffset PublishedOn { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Builds the adapter.json that goes inside every package.
    ///
    /// Two sources with a clear split. The AUTHOR owns presentation — name, description, icon,
    /// publisher, tags, properties — in an optional adapter.json beside the project. The INSTALLER
    /// owns the facts — id, version, entry, lifecycle, runtime, SDK version — and overwrites
    /// whatever the author file says about them, because a manifest that disagrees with the package
    /// it is in is worse than none: a host would launch the wrong file, or a catalog would offer a
    /// resident adapter somewhere only a classic one can run.
    /// </summary>
    public static class ManifestBuilder
    {
        /// <summary>The largest icon carried inline in the catalog. Bigger ones stay in the package only.</summary>
        public const int MaxInlineIconBytes = 64 * 1024;

        const string SdkAssemblyFile = "SW.Serverless.Sdk.dll";

        /// <summary>The author's adapter.json beside the project file, or null when there is none.</summary>
        public static AdapterManifest LoadAuthorFile(string projectPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            var path = Path.Combine(directory!, AdapterManifest.FileName);
            if (!File.Exists(path)) return null;

            try
            {
                return AdapterManifest.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                // Failing beats publishing without the author's manifest: they wrote one, and a
                // package that silently lost its name and properties would look like it worked.
                throw new SWException($"{path} is not a valid adapter manifest: {ex.Message}");
            }
        }

        public static string LanguageOf(string projectPath) =>
            Path.GetExtension(projectPath)?.ToLowerInvariant() switch
            {
                ".fsproj" => "fsharp",
                ".vbproj" => "vb",
                _ => "csharp",
            };

        public static AdapterManifest Build(AdapterManifest author, GeneratedManifestFields generated)
        {
            // Starting from the author's object keeps every field this version of the model does
            // not know (its Extensions), so a newer author file is not trimmed by an older installer.
            var manifest = author ?? new AdapterManifest();
            var description = generated.Description ?? new AdapterDescription();

            // Never lowered: an author file from a newer tool keeps saying so.
            manifest.ManifestVersion = Math.Max(manifest.ManifestVersion, AdapterManifest.CurrentManifestVersion);
            manifest.Id = generated.Id;
            manifest.Version = string.IsNullOrWhiteSpace(generated.Version) ? null : generated.Version;
            manifest.Runtime = AdapterManifest.DotnetRuntime;
            manifest.Language = string.IsNullOrWhiteSpace(manifest.Language) ? generated.DefaultLanguage : manifest.Language;
            manifest.Entry = generated.Entry;
            manifest.Lifecycle = description.Lifecycle == AdapterDescription.ResidentLifecycle
                ? AdapterManifest.ResidentLifecycle
                : AdapterManifest.ClassicLifecycle;

            // Resident adapters speak protocol 2 and nothing else today; a classic one speaks no
            // resident protocol at all, so it carries no range.
            manifest.Protocol = manifest.IsResident ? new AdapterProtocolRange { Min = 2, Max = 2 } : null;

            manifest.Kinds = PickKinds(generated.KindOverride, manifest.Kinds, description.Kind);
            manifest.SdkVersion = generated.SdkVersion;
            manifest.PublishedOn = generated.PublishedOn;

            if (!string.IsNullOrWhiteSpace(generated.ReleaseNotes)) manifest.ReleaseNotes = generated.ReleaseNotes;

            manifest.Tags ??= new List<string>();
            manifest.Categories ??= new List<string>();
            manifest.Properties ??= new List<AdapterProperty>();

            return manifest;
        }

        /// <summary>--kind first; then what the author declared; then what the assembly declared.</summary>
        static List<string> PickKinds(string overrideKinds, List<string> authorKinds, string describedKinds)
        {
            static List<string> Split(string kinds) =>
                (kinds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(overrideKinds)) return Split(overrideKinds);
            var declared = (authorKinds ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return declared.Count > 0 ? declared : Split(describedKinds);
        }

        /// <summary>
        /// The SDK version the adapter was built against, or null when it does not use the SDK.
        /// Read from the entry assembly's deps.json first: it names the NuGet package version, where
        /// the published SDK's assembly and file versions say 1.0.0 whatever the package is. The
        /// SW.Serverless.Sdk.dll in the output is the fallback.
        /// </summary>
        public static string SdkVersionIn(string publishDirectory, string entryAssembly = null)
        {
            var fromDeps = SdkVersionFromDeps(publishDirectory, entryAssembly);
            if (fromDeps != null) return fromDeps;

            var path = Path.Combine(publishDirectory, SdkAssemblyFile);
            if (!File.Exists(path)) return null;

            try
            {
                var product = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                if (!string.IsNullOrWhiteSpace(product))
                {
                    // "10.0.1+3f2a…" — the build metadata is a commit, not part of the version.
                    var plus = product.IndexOf('+');
                    var version = (plus >= 0 ? product[..plus] : product).Trim();
                    if (AdapterCatalogPaths.IsVersion(version)) return version;
                }

                var assemblyVersion = AssemblyName.GetAssemblyName(path).Version;
                return assemblyVersion == null ? null : assemblyVersion.ToString(3);
            }
            catch
            {
                // Informational: a version that cannot be read is left out, not a failed publish.
                return null;
            }
        }

        const string SdkPackage = "SimplyWorks.Serverless.Sdk/";

        static string SdkVersionFromDeps(string publishDirectory, string entryAssembly)
        {
            if (string.IsNullOrWhiteSpace(entryAssembly)) return null;
            try
            {
                var deps = Path.Combine(publishDirectory, Path.GetFileNameWithoutExtension(entryAssembly) + ".deps.json");
                if (!File.Exists(deps)) return null;

                using var document = JsonDocument.Parse(File.ReadAllText(deps));
                if (!document.RootElement.TryGetProperty("libraries", out var libraries)) return null;

                foreach (var library in libraries.EnumerateObject())
                    if (library.Name.StartsWith(SdkPackage, StringComparison.OrdinalIgnoreCase))
                    {
                        var version = library.Name[SdkPackage.Length..];
                        if (AdapterCatalogPaths.IsVersion(version)) return version;
                    }
            }
            catch
            {
                // Informational only.
            }
            return null;
        }

        /// <summary>
        /// Checks the icon the manifest names, and returns it as a data: URI for the catalog — or
        /// null when there is no icon, or it is too big or not a known image type to inline (a
        /// warning, since the package still carries it).
        /// </summary>
        public static string IconDataUri(AdapterManifest manifest, string publishDirectory, Action<string> warn)
        {
            if (string.IsNullOrWhiteSpace(manifest.Icon)) return null;

            var path = Path.Combine(publishDirectory, manifest.Icon.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new SWException(
                    $"The icon '{manifest.Icon}' named in adapter.json is not in the published output. " +
                    "Include it in the project with CopyToPublishDirectory, or remove it from adapter.json.");

            var bytes = File.ReadAllBytes(path);
            return DataUri(manifest.Icon, bytes, warn);
        }

        public static string DataUri(string iconPath, byte[] bytes, Action<string> warn)
        {
            var mime = Path.GetExtension(iconPath)?.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                _ => null,
            };

            if (mime == null)
            {
                warn?.Invoke($"The icon '{iconPath}' is not a PNG, JPEG or SVG; it is packaged but not shown in the catalog.");
                return null;
            }

            if (bytes.Length > MaxInlineIconBytes)
            {
                warn?.Invoke(
                    $"The icon '{iconPath}' is {bytes.Length / 1024} KB, over the {MaxInlineIconBytes / 1024} KB the catalog " +
                    "carries inline; it is packaged but not shown in the catalog.");
                return null;
            }

            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        /// <summary>Fails with every problem at once, so an author fixes their file in one pass.</summary>
        public static void Validate(AdapterManifest manifest)
        {
            var problems = manifest.Validate();
            if (problems.Count == 0) return;

            throw new SWException(
                "The adapter manifest is not valid, so nothing was uploaded:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }

        public static void WriteInto(AdapterManifest manifest, string publishDirectory) =>
            // Overwrites the author's file if the build copied it: the package carries the merged one.
            File.WriteAllText(Path.Combine(publishDirectory, AdapterManifest.FileName), manifest.ToJson());
    }
}
