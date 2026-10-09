using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml.Linq;
using SW.Serverless.Contract.Catalog;

namespace SW.Serverless.Tooling.Building
{
    public class BuildRequest
    {
        /// <summary>The adapter's project folder, with its adapter.json.</summary>
        public string ProjectDirectory { get; set; }

        /// <summary>Where the package folder and zip go; bin/serverless in the project unless set, which the source rules leave out.</summary>
        public string OutputDirectory { get; set; }

        /// <summary>Carry the source in the package. On unless a partner won't hand it over.</summary>
        public bool IncludeSource { get; set; } = true;

        /// <summary>Source files whose secret-scan findings are false positives, relative to the project.</summary>
        public ISet<string> AllowedFiles { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Report what would be carried without building anything.</summary>
        public bool DryRun { get; set; }

        public Runtimes.AdapterRuntimeOptions Runtimes { get; set; } = new();
        public Action<string> Log { get; set; } = _ => { };
    }

    public class BuildResult
    {
        public bool Succeeded => Problems.Count == 0;
        public List<string> Problems { get; } = new();
        public List<string> Warnings { get; } = new();
        public AdapterManifest Manifest { get; set; }
        public string PackageDirectory { get; set; }
        public string ZipPath { get; set; }

        /// <summary>The source carried, relative to its root, with sizes.</summary>
        public List<(string Path, long Bytes)> SourceFiles { get; } = new();
        public List<SecretFinding> Findings { get; } = new();
    }

    /// <summary>
    /// What serverless build does, for an adapter in any language its SDK supports: builds it,
    /// asks it to describe itself, writes its manifest from that and the author's adapter.json,
    /// carries its source under the source rules, and zips the result into a package any host runs.
    /// </summary>
    public static class PackageBuilder
    {
        /// <summary>The source folder size past which the build warns: usually build output slipped in.</summary>
        public const long SourceWarningBytes = 10 * 1024 * 1024;

        static readonly string[] Lockfiles =
            { "packages.lock.json", "poetry.lock", "requirements.lock", "requirements.txt", "uv.lock", "Pipfile.lock",
              "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "go.sum" };

        public static async Task<BuildResult> BuildAsync(BuildRequest request)
        {
            var result = new BuildResult();
            var project = Path.GetFullPath(request.ProjectDirectory);

            var authorPath = Path.Combine(project, AdapterManifest.FileName);
            if (!File.Exists(authorPath))
            {
                result.Problems.Add($"there is no {AdapterManifest.FileName} in {project}; serverless init writes one");
                return result;
            }
            var authorJson = await File.ReadAllTextAsync(authorPath);
            var author = AdapterManifest.Parse(authorJson);
            if (string.IsNullOrWhiteSpace(author.Id))
            {
                result.Problems.Add($"{AdapterManifest.FileName} needs an id");
                return result;
            }

            var runtime = string.IsNullOrWhiteSpace(author.Runtime) ? AdapterManifest.DotnetRuntime : author.Runtime;
            if (string.Equals(runtime, AdapterManifest.PythonRuntime, StringComparison.OrdinalIgnoreCase))
            {
                await PythonBuild.BuildAsync(request, project, author, result);
                return result;
            }
            if (!string.Equals(runtime, AdapterManifest.DotnetRuntime, StringComparison.OrdinalIgnoreCase))
            {
                result.Problems.Add($"building a '{runtime}' adapter arrives with that language's SDK; this build does .NET and Python");
                return result;
            }

            var projectFiles = Directory.GetFiles(project, "*.*proj").Where(f => f.EndsWith(".csproj") || f.EndsWith(".fsproj") || f.EndsWith(".vbproj")).ToList();
            if (projectFiles.Count != 1)
            {
                result.Problems.Add(projectFiles.Count == 0
                    ? $"there is no .NET project file in {project}"
                    : $"there are {projectFiles.Count} project files in {project}; keep one adapter per folder");
                return result;
            }
            var projectFile = projectFiles[0];

            // Source first: a secret found here stops the build before anything is produced.
            var source = request.IncludeSource ? CollectSource(project, projectFile, request, result) : null;
            if (!result.Succeeded || request.DryRun) return result;

            var output = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(project, "bin", "serverless"));
            var packageDirectory = Path.Combine(output, "package");
            if (Directory.Exists(packageDirectory)) Directory.Delete(packageDirectory, true);
            Directory.CreateDirectory(packageDirectory);

            request.Log("Building...");
            if (!new InstallerLogic().BuildPublish(projectFile, packageDirectory))
            {
                result.Problems.Add("dotnet publish failed; its output above says why");
                return result;
            }

            var entry = InstallerLogic.ResolveEntryAssembly(packageDirectory, projectFile);
            if (entry == null)
            {
                result.Problems.Add("the built output has no entry assembly");
                return result;
            }

            request.Log("Asking the adapter to describe itself...");
            var (description, problem) = await LocalAdapterHost.DescribeAsync(Path.Combine(packageDirectory, entry),
                AdapterManifest.DotnetRuntime, request.Runtimes);
            if (description == null)
            {
                result.Problems.Add($"{problem}. serverless build needs SimplyWorks.Serverless.Sdk 10.1.0 or later; " +
                                    "an adapter on an older SDK is published with serverless <project> <id> as before");
                return result;
            }
            result.Warnings.AddRange(description.Warnings);

            var manifest = ManifestFrom(author, AuthorChoseClassic(authorJson), description, entry, ManifestBuilder.LanguageOf(projectFile));

            if (source != null)
            {
                var sourceDirectory = Path.Combine(packageDirectory, AdapterSource.DefaultPath);
                manifest.Source = new AdapterSource
                {
                    BuildCommand = "dotnet publish -c Release",
                    Lockfiles = source.Keys.Where(IsLockfile).OrderBy(k => k).ToList(),
                };
                foreach (var (relative, absolute) in source.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    var target = Path.Combine(sourceDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(absolute, target);
                    manifest.Source.Files[relative] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(absolute))).ToLowerInvariant();
                }
                if (manifest.Source.Lockfiles.Count == 0) manifest.Source.Lockfiles = null;
            }

            var problems = manifest.Validate();
            if (problems.Count > 0)
            {
                result.Problems.AddRange(problems);
                return result;
            }

            await File.WriteAllTextAsync(Path.Combine(packageDirectory, AdapterManifest.FileName), manifest.ToJson());

            var zip = Path.Combine(output, $"{manifest.Id}{(string.IsNullOrWhiteSpace(manifest.Version) ? "" : "-" + manifest.Version)}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(packageDirectory, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

            result.Manifest = manifest;
            result.PackageDirectory = packageDirectory;
            result.ZipPath = zip;
            return result;
        }

        /// <summary>
        /// The manifest: the author's file — id, names, marketplace text, presentation of settings —
        /// with what the adapter says about itself, which wins wherever the two speak to the same thing.
        /// </summary>
        internal static AdapterManifest ManifestFrom(AdapterManifest author, bool authorChoseClassic,
            AdapterSelfDescription description, string entry, string language)
        {
            var manifest = AdapterManifest.Parse(author.ToJson());
            manifest.Entry = entry;
            manifest.Runtime = AdapterManifest.DotnetRuntime;
            manifest.Language ??= language;
            manifest.SdkVersion = description.SdkVersion;

            if (description.Lifecycle == AdapterManifest.ResidentLifecycle)
            {
                // It speaks gRPC. Run classically only when the author says so — the opt-in for a
                // .NET adapter on the gRPC protocol — and as a resident otherwise.
                manifest.Lifecycle = authorChoseClassic ? AdapterManifest.ClassicLifecycle : AdapterManifest.ResidentLifecycle;
                manifest.Protocol = new AdapterProtocolRange { Min = 2, Max = 2 };
            }
            else
            {
                manifest.Lifecycle = AdapterManifest.ClassicLifecycle;
                manifest.Protocol = null;
            }

            manifest.Kinds = (author.Kinds ?? new List<string>()).Union(description.Kinds, StringComparer.OrdinalIgnoreCase).ToList();
            var contracts = new Dictionary<string, int>(author.Contracts ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, version) in description.Contracts) contracts[name] = version;
            manifest.Contracts = contracts.Count == 0 ? null : contracts;

            var authored = (author.Properties ?? new List<AdapterProperty>()).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            manifest.Properties = description.Settings.Select(setting =>
            {
                var property = authored.TryGetValue(setting.Name, out var a) ? a : new AdapterProperty { Name = setting.Name };
                property.Name = setting.Name;
                property.Required = setting.Required;
                property.Secret = setting.Secret;
                property.Default = setting.Secret ? null : setting.Default;
                if (!string.IsNullOrWhiteSpace(setting.Description)) property.Description = setting.Description;
                if (string.IsNullOrWhiteSpace(property.Type) || property.Type == AdapterProperty.TextType)
                    property.Type = string.IsNullOrWhiteSpace(setting.Type) ? AdapterProperty.TextType : setting.Type;
                return property;
            }).ToList();

            return manifest;
        }

        internal static bool IsLockfile(string path) =>
            Lockfiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the author's own adapter.json says "lifecycle": "classic" — read from the file,
        /// since the model fills in classic when nothing is said.
        /// </summary>
        internal static bool AuthorChoseClassic(string authorJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(authorJson, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return document.RootElement.TryGetProperty("lifecycle", out var lifecycle) &&
                   string.Equals(lifecycle.GetString(), AdapterManifest.ClassicLifecycle, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The source to carry, keyed by its path in the package's source folder: the project and
        /// the local projects it references, under the ignore rules, scanned for secrets.
        /// </summary>
        internal static Dictionary<string, string> CollectSource(string project, string projectFile, BuildRequest request, BuildResult result)
        {
            var roots = new List<string> { project };
            // A .NET project's local project references come along; other languages have none to follow.
            if (projectFile != null)
                foreach (var referenced in LocalReferences(projectFile, new HashSet<string>(StringComparer.Ordinal)))
                    if (!roots.Contains(referenced)) roots.Add(referenced);

            var common = CommonAncestor(roots);
            var repository = RepositoryRoot(project);
            if (repository != null && !common.StartsWith(repository, StringComparison.Ordinal))
            {
                result.Warnings.Add($"a referenced project is outside the repository ({common}); the source carried may not rebuild on another machine");
            }

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var root in roots)
            {
                var rules = IgnoreRules.For(root);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var withinRoot = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (rules.Ignores(withinRoot)) continue;
                    var relative = Path.GetRelativePath(common, file).Replace('\\', '/');
                    if (files.ContainsKey(relative)) continue;

                    var bytes = File.ReadAllBytes(file);
                    if (SecretScanner.IsText(bytes))
                    {
                        var withinProject = Path.GetRelativePath(project, file).Replace('\\', '/');
                        if (!request.AllowedFiles.Contains(withinProject) && !request.AllowedFiles.Contains(relative))
                            result.Findings.AddRange(SecretScanner.Scan(relative, System.Text.Encoding.UTF8.GetString(bytes)));
                    }

                    files[relative] = file;
                    total += bytes.Length;
                    result.SourceFiles.Add((relative, bytes.Length));
                }
            }

            foreach (var finding in result.Findings)
                result.Problems.Add($"{finding.File}:{finding.Line} looks like {finding.Kind}. Remove it, or pass --allow {finding.File} if it isn't one");

            if (total > SourceWarningBytes)
                result.Warnings.Add($"the source is {total / (1024 * 1024)} MB; build output may have slipped in — add it to .serverlessignore");

            return files;
        }

        /// <summary>The folders of the projects a project references, all the way down.</summary>
        static IEnumerable<string> LocalReferences(string projectFile, HashSet<string> seen)
        {
            if (!seen.Add(projectFile)) yield break;
            XDocument document;
            try { document = XDocument.Load(projectFile); }
            catch { yield break; }

            foreach (var include in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                         .Select(e => (string)e.Attribute("Include")).Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (!File.Exists(path)) continue;
                yield return Path.GetDirectoryName(path)!;
                foreach (var deeper in LocalReferences(path, seen)) yield return deeper;
            }
        }

        static string CommonAncestor(IEnumerable<string> folders)
        {
            var split = folders.Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar)).ToList();
            var length = split.Min(s => s.Length);
            var i = 0;
            while (i < length && split.All(s => s[i] == split[0][i])) i++;
            var common = string.Join(Path.DirectorySeparatorChar, split[0].Take(i));
            return string.IsNullOrEmpty(common) ? Path.DirectorySeparatorChar.ToString() : common;
        }

        static string RepositoryRoot(string folder)
        {
            for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
            return null;
        }
    }
}
