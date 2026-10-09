using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SW.Serverless.Contract.Catalog;

namespace SW.Serverless.Tooling.Building
{
    /// <summary>
    /// sw-serverless build for a JavaScript or TypeScript adapter. Node runs JavaScript from its source,
    /// so the package is the adapter's files as they are, TypeScript among them with its types
    /// stripped by Node itself — no compiler, no packages — and a node_modules beside them: the SDK
    /// from the copy this tool carries, any packages the request hands it, and whatever
    /// package.json depends on, through npm.
    /// </summary>
    /// <remarks>
    /// A dependency with a native addon is built for the machine npm runs on, which is not something
    /// that can be done for another platform from here. It is refused unless adapter.json limits the
    /// adapter to platforms this machine is one of, and the manifest then lists them.
    /// </remarks>
    public static class NodeBuild
    {
        public const string DefaultEntry = "main.js";
        public const string DefaultRuntimeVersion = ">=22";

        /// <summary>The SDK, which the build vendors itself; package.json naming it is not sent to npm.</summary>
        static readonly string[] OwnPackages = { "@simplyworks/sw-serverless" };
        static readonly string[] TypeScript = { ".ts", ".mts", ".cts" };

        internal static async Task BuildAsync(BuildRequest request, string project, AdapterManifest author, BuildResult result)
        {
            var entry = string.IsNullOrWhiteSpace(author.Entry) ? DefaultEntry : author.Entry.Replace('\\', '/');
            if (!File.Exists(Path.Combine(project, entry)))
            {
                result.Problems.Add($"there is no {entry} in {project}; set \"entry\" in {AdapterManifest.FileName} to the script that runs the adapter");
                return;
            }

            // Source first, as for every language: a secret found here stops the build before anything is produced.
            var source = PackageBuilder.CollectSource(project, null, request, result);
            if (!result.Succeeded || request.DryRun) return;

            var output = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(project, "bin", "serverless"));
            var packageDirectory = Path.Combine(output, "package");
            if (Directory.Exists(packageDirectory)) Directory.Delete(packageDirectory, true);
            Directory.CreateDirectory(packageDirectory);

            foreach (var (relative, absolute) in source)
            {
                var target = Path.Combine(packageDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(absolute, target);
            }

            var typeScript = source.Keys.Where(IsTypeScript).Where(k => !k.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)).ToList();
            if (typeScript.Count > 0)
            {
                request.Log($"Stripping the types from {typeScript.Count} TypeScript file{(typeScript.Count == 1 ? "" : "s")}...");
                if (!await StripTypesAsync(request, packageDirectory, typeScript, result)) return;
                if (IsTypeScript(entry)) entry = JavaScriptName(entry);
            }

            var modules = Path.Combine(packageDirectory, "node_modules");
            var platforms = await InstallDependenciesAsync(request, project, author, packageDirectory, result);
            if (!result.Succeeded) return;
            WriteOwnPackages(modules);
            PythonBuild.WritePackages(request, AdapterManifest.NodeRuntime, modules);

            request.Log("Asking the adapter to describe itself...");
            var (description, problem) = await LocalAdapterHost.DescribeAsync(Path.Combine(packageDirectory, entry),
                AdapterManifest.NodeRuntime, request.Runtimes);
            if (description == null)
            {
                result.Problems.Add($"{problem}. A Node adapter describes itself through run() from @simplyworks/sw-serverless; make sure {entry} calls it");
                return;
            }
            result.Warnings.AddRange(description.Warnings);

            var manifest = PackageBuilder.ManifestFrom(author, false, description, entry, typeScript.Count > 0 ? "typescript" : "javascript");
            manifest.Runtime = AdapterManifest.NodeRuntime;
            manifest.RuntimeVersion ??= DefaultRuntimeVersion;
            manifest.Platforms = platforms;
            manifest.Lifecycle = description.Lifecycle == AdapterManifest.ResidentLifecycle
                ? AdapterManifest.ResidentLifecycle
                : AdapterManifest.ClassicLifecycle;
            manifest.Protocol = new AdapterProtocolRange { Min = 2, Max = 2 };

            if (request.IncludeSource)
            {
                manifest.Source = new AdapterSource
                {
                    BuildCommand = "sw-serverless build",
                    Lockfiles = source.Keys.Where(PackageBuilder.IsLockfile).OrderBy(k => k).ToList(),
                };
                var sourceDirectory = Path.Combine(packageDirectory, AdapterSource.DefaultPath);
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
                return;
            }

            await File.WriteAllTextAsync(Path.Combine(packageDirectory, AdapterManifest.FileName), manifest.ToJson());

            var zip = Path.Combine(output, $"{manifest.Id}{(string.IsNullOrWhiteSpace(manifest.Version) ? "" : "-" + manifest.Version)}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(packageDirectory, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

            result.Manifest = manifest;
            result.PackageDirectory = packageDirectory;
            result.ZipPath = zip;
        }

        static bool IsTypeScript(string path) => TypeScript.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        static string JavaScriptName(string path) => Path.ChangeExtension(path, Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mts" => ".mjs",
            ".cts" => ".cjs",
            _ => ".js",
        }).Replace('\\', '/');

        /// <summary>
        /// Each TypeScript file becomes JavaScript beside it, by Node's own type stripping (Node 22.13
        /// or later): what TypeScript erases is erased, and anything it would have to compile — an
        /// enum, a namespace — is refused with its file and line, rather than built differently.
        /// </summary>
        static async Task<bool> StripTypesAsync(BuildRequest request, string packageDirectory, List<string> files, BuildResult result)
        {
            const string script = """
                const fs = require("node:fs");
                const { stripTypeScriptTypes } = require("node:module");
                if (typeof stripTypeScriptTypes !== "function") {
                  console.error("this Node can't strip TypeScript types; use Node 22.13 or later");
                  process.exit(2);
                }
                let failed = false;
                for (const [from, to] of JSON.parse(fs.readFileSync(0, "utf8"))) {
                  try {
                    fs.writeFileSync(to, stripTypeScriptTypes(fs.readFileSync(from, "utf8"), { mode: "strip" }));
                    fs.rmSync(from);
                  } catch (e) {
                    console.error(`${from}: ${e.message}`);
                    failed = true;
                  }
                }
                process.exit(failed ? 1 : 0);
                """;
            var pairs = files.Select(f => new[] { Path.Combine(packageDirectory, f), Path.Combine(packageDirectory, JavaScriptName(f)) }).ToList();
            var (ok, output) = await RunAsync(request.Runtimes.NodeExecutable, packageDirectory, JsonSerializer.Serialize(pairs),
                "--no-warnings", "-e", script);
            if (!ok)
                result.Problems.Add($"the TypeScript could not be turned into JavaScript: {output.Trim()}. " +
                                    "Only syntax TypeScript erases is supported: no enums, namespaces or parameter properties");
            return ok;
        }

        /// <summary>
        /// npm installs package.json's dependencies, other than the SDKs, into the package. Returns
        /// the platforms the package is limited to: none, unless a dependency has a native addon.
        /// </summary>
        static async Task<List<string>> InstallDependenciesAsync(BuildRequest request, string project, AdapterManifest author,
            string packageDirectory, BuildResult result)
        {
            var authored = author.Platforms is { Count: > 0 } ? author.Platforms : null;
            var packageJson = Path.Combine(packageDirectory, "package.json");
            if (!File.Exists(packageJson)) return authored;

            var document = JsonNode.Parse(await File.ReadAllTextAsync(packageJson))?.AsObject();
            var dependencies = document?["dependencies"]?.AsObject();
            if (dependencies == null) return authored;
            foreach (var own in OwnPackages) dependencies.Remove(own);
            foreach (var provided in request.Packages.Where(p => string.Equals(p.Runtime, AdapterManifest.NodeRuntime, StringComparison.OrdinalIgnoreCase)))
                dependencies.Remove(provided.Name);
            document.Remove("devDependencies");
            if (dependencies.Count == 0) return authored;

            // npm works on a copy, so the package's own package.json keeps what the author wrote.
            var work = Path.Combine(Path.GetTempPath(), "swsl-nodebuild", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(work, "package.json"), document.ToJsonString());
                var lockfile = Path.Combine(project, "package-lock.json");
                var useLock = File.Exists(lockfile);
                if (useLock) File.Copy(lockfile, Path.Combine(work, "package-lock.json"));

                request.Log("Installing the dependencies...");
                // Scripts are not run: a dependency's install script is code nobody reviewed, run on
                // the build machine. An addon that needs one to build is refused below anyway.
                var (ok, output) = await RunAsync("npm", work, null,
                    useLock ? "ci" : "install", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund", "--loglevel=error");
                if (!ok)
                {
                    result.Problems.Add($"npm could not install the dependencies: {string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3))}");
                    return null;
                }

                var modules = Path.Combine(work, "node_modules");
                if (!Directory.Exists(modules)) return authored;
                var addons = Directory.EnumerateFiles(modules, "*.node", SearchOption.AllDirectories).ToList();
                if (addons.Count > 0)
                {
                    var here = Runtimes.AdapterRuntimes.CurrentPlatform;
                    if (authored == null || authored.Count != 1 || !string.Equals(authored[0], here, StringComparison.OrdinalIgnoreCase))
                    {
                        var addon = Path.GetRelativePath(modules, addons[0]).Replace('\\', '/');
                        var package = addon.StartsWith('@') ? string.Join('/', addon.Split('/').Take(2)) : addon.Split('/')[0];
                        result.Problems.Add($"{package} has native code ({addon}), and which of it loads depends on the platform npm installed it on. " +
                                            $"Build on the platform the adapter runs on, with \"platforms\": [\"<that platform>\"] in {AdapterManifest.FileName}; here that is {here}");
                        return null;
                    }
                }

                CopyFolder(modules, Path.Combine(packageDirectory, "node_modules"));
                return authored;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        /// <summary>The SDK, as this tool carries it, under <paramref name="modules"/>.</summary>
        public static void WriteOwnPackages(string modules)
        {
            var assembly = typeof(NodeBuild).Assembly;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                var normalized = name.Replace('\\', '/');
                if (!normalized.StartsWith("node/", StringComparison.Ordinal)) continue;
                var target = Path.Combine(modules, normalized["node/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var resource = assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(target);
                resource.CopyTo(file);
            }
        }

        static void CopyFolder(string from, string to)
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        static async Task<(bool Ok, string Output)> RunAsync(string executable, string workingDirectory, string stdin, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = stdin != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode == 0, await stdout + await stderr);
        }
    }
}
