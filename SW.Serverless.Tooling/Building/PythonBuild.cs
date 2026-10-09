using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SW.Serverless.Contract.Catalog;

namespace SW.Serverless.Tooling.Building
{
    /// <summary>
    /// serverless build for a Python adapter. Python runs from its source, so the package is the
    /// adapter's files as they are, with what they import vendored under <see cref="VendorFolder"/>:
    /// the SDK and the Bitween kinds from copies this tool carries — no PyPI, no network — and
    /// whatever requirements.txt names, through pip. A small entry script puts the vendored code on
    /// the path and runs the adapter's own entry.
    /// </summary>
    /// <remarks>
    /// Pure-Python requirements are vendored once and the package runs anywhere. A requirement that
    /// has native code is vendored once per target platform, under its own folder, and the manifest
    /// lists those platforms, so a host on any other refuses it at install rather than failing on
    /// import.
    /// </remarks>
    public static class PythonBuild
    {
        public const string VendorFolder = "_vendor";
        public const string EntryScript = "_serverless_entry.py";
        public const string DefaultEntry = "main.py";
        public const string DefaultRuntimeVersion = ">=3.12";

        /// <summary>The platforms a package with native requirements is built for, unless adapter.json names others.</summary>
        public static readonly IReadOnlyList<string> DefaultNativePlatforms = new[] { "linux-x64", "linux-arm64" };

        /// <summary>The SDKs the build vendors itself; requirements.txt naming them is not sent to pip.</summary>
        static readonly string[] OwnPackages = { "simplyworks-serverless", "simplyworks_serverless", "simplyworks-bitween", "simplyworks_bitween" };

        // pip's platform tags for each platform a manifest can name.
        static readonly Dictionary<string, string[]> PipPlatforms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["linux-x64"] = new[] { "manylinux2014_x86_64", "manylinux_2_28_x86_64", "manylinux_2_17_x86_64" },
            ["linux-arm64"] = new[] { "manylinux2014_aarch64", "manylinux_2_28_aarch64", "manylinux_2_17_aarch64" },
            ["linux-musl-x64"] = new[] { "musllinux_1_2_x86_64" },
            ["linux-musl-arm64"] = new[] { "musllinux_1_2_aarch64" },
            ["osx-arm64"] = new[] { "macosx_11_0_arm64" },
            ["osx-x64"] = new[] { "macosx_10_9_x86_64" },
            ["win-x64"] = new[] { "win_amd64" },
        };

        /// <summary>The Python the bootstrap and the vendored code are written for, as pip's --python-version takes it.</summary>
        const string TargetPythonVersion = "3.12";

        internal static async Task BuildAsync(BuildRequest request, string project, AdapterManifest author, BuildResult result)
        {
            var entry = string.IsNullOrWhiteSpace(author.Entry) ? DefaultEntry : author.Entry.Replace('\\', '/');
            if (!File.Exists(Path.Combine(project, entry)))
            {
                result.Problems.Add($"there is no {entry} in {project}; set \"entry\" in {AdapterManifest.FileName} to the script that runs the adapter");
                return;
            }

            // Source first, as for .NET: a secret found here stops the build before anything is produced.
            var source = PackageBuilder.CollectSource(project, null, request, result);
            if (!result.Succeeded || request.DryRun) return;

            var output = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(project, "bin", "serverless"));
            var packageDirectory = Path.Combine(output, "package");
            if (Directory.Exists(packageDirectory)) Directory.Delete(packageDirectory, true);
            Directory.CreateDirectory(packageDirectory);

            // The adapter's own files run as they are: everything the source rules keep, which leaves
            // out caches, virtual environments and secrets.
            foreach (var (relative, absolute) in source)
            {
                var target = Path.Combine(packageDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(absolute, target);
            }

            var vendor = Path.Combine(packageDirectory, VendorFolder);
            WriteOwnPackages(vendor);

            var (platforms, describeOnly) = await VendorRequirementsAsync(request, project, author, vendor, result);
            if (!result.Succeeded) return;

            await File.WriteAllTextAsync(Path.Combine(packageDirectory, EntryScript), Bootstrap(entry));

            request.Log("Asking the adapter to describe itself...");
            var (description, problem) = await LocalAdapterHost.DescribeAsync(Path.Combine(packageDirectory, EntryScript),
                AdapterManifest.PythonRuntime, request.Runtimes);
            if (description == null)
            {
                result.Problems.Add($"{problem}. A Python adapter describes itself through simplyworks_serverless.run(); make sure {entry} calls it");
                return;
            }
            result.Warnings.AddRange(description.Warnings);
            // Vendored for this machine only so it could describe itself; not one of its platforms.
            if (describeOnly != null) Directory.Delete(describeOnly, true);

            var manifest = PackageBuilder.ManifestFrom(author, false, description, EntryScript, "python");
            manifest.Runtime = AdapterManifest.PythonRuntime;
            manifest.RuntimeVersion ??= DefaultRuntimeVersion;
            manifest.Platforms = platforms;
            // Every Python adapter speaks gRPC, whichever way it runs.
            manifest.Lifecycle = description.Lifecycle == AdapterManifest.ResidentLifecycle
                ? AdapterManifest.ResidentLifecycle
                : AdapterManifest.ClassicLifecycle;
            manifest.Protocol = new AdapterProtocolRange { Min = 2, Max = 2 };

            if (request.IncludeSource)
            {
                manifest.Source = new AdapterSource
                {
                    BuildCommand = "serverless build",
                    Lockfiles = source.Keys.Where(k => PackageBuilder.IsLockfile(k)).OrderBy(k => k).ToList(),
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

        /// <summary>The SDK and the Bitween kinds, as this tool carries them, under <paramref name="vendor"/>.</summary>
        public static void WriteOwnPackages(string vendor)
        {
            var assembly = typeof(PythonBuild).Assembly;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                var normalized = name.Replace('\\', '/');
                if (!normalized.StartsWith("python/", StringComparison.Ordinal)) continue;
                var target = Path.Combine(vendor, normalized["python/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var resource = assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(target);
                resource.CopyTo(file);
            }
        }

        /// <summary>
        /// Vendors requirements.txt. Returns the platforms the package is limited to — none when
        /// everything is pure Python, or the targets when anything is native — and, when this
        /// machine isn't a target, a folder vendored for it alone, to describe the adapter with.
        /// </summary>
        static async Task<(List<string> Platforms, string DescribeOnly)> VendorRequirementsAsync(BuildRequest request, string project, AdapterManifest author,
            string vendor, BuildResult result)
        {
            var requirementsFile = Path.Combine(project, "requirements.txt");
            if (!File.Exists(requirementsFile)) return (author.Platforms is { Count: > 0 } ? author.Platforms : null, null);

            var lines = (await File.ReadAllLinesAsync(requirementsFile))
                .Where(l => !IsOwnPackage(l))
                .ToList();
            if (!lines.Any(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')))
                return (author.Platforms is { Count: > 0 } ? author.Platforms : null, null);

            var work = Path.Combine(Path.GetTempPath(), "swsl-pybuild", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var filtered = Path.Combine(work, "requirements.txt");
                await File.WriteAllLinesAsync(filtered, lines);

                var targets = author.Platforms is { Count: > 0 } ? author.Platforms.ToList() : DefaultNativePlatforms.ToList();
                foreach (var target in targets.Where(t => !PipPlatforms.ContainsKey(t)))
                    result.Problems.Add($"'{target}' isn't a platform Python packages can be built for: use {string.Join(", ", PipPlatforms.Keys)}");
                if (!result.Succeeded) return (null, null);

                // Wheels only, for the Python the host runs: nothing is compiled here, so what is
                // vendored is exactly what pip would install there.
                request.Log("Fetching the requirements...");
                var wheels = new Dictionary<string, string>();
                foreach (var target in targets)
                {
                    var folder = Path.Combine(work, "wheels", target);
                    var (ok, output) = await PipAsync(request, new[] { "download", "-r", filtered, "-d", folder }
                        .Concat(WheelOptions(target)).ToArray());
                    if (!ok)
                    {
                        result.Problems.Add($"pip could not get the requirements for {target}: {LastLines(output)}. " +
                                            "A package with native code needs a wheel for every platform the adapter runs on");
                        return (null, null);
                    }
                    wheels[target] = folder;
                }

                var native = wheels.Values.SelectMany(f => Directory.GetFiles(f, "*.whl")).Any(w => !Path.GetFileName(w).EndsWith("-none-any.whl", StringComparison.OrdinalIgnoreCase));
                if (!native)
                {
                    // One set serves every platform.
                    var (ok, output) = await PipAsync(request, new[] { "install", "--no-deps", "--no-compile", "--target", vendor }
                        .Concat(Directory.GetFiles(wheels[targets[0]], "*.whl")).ToArray());
                    if (!ok) result.Problems.Add($"pip could not vendor the requirements: {LastLines(output)}");
                    return (author.Platforms is { Count: > 0 } ? author.Platforms : null, null);
                }

                foreach (var target in targets)
                {
                    var (ok, output) = await PipAsync(request, new[] { "install", "--no-deps", "--no-compile", "--target", Path.Combine(vendor, target) }
                        .Concat(WheelOptions(target)).Concat(Directory.GetFiles(wheels[target], "*.whl")).ToArray());
                    if (!ok)
                    {
                        result.Problems.Add($"pip could not vendor the requirements for {target}: {LastLines(output)}");
                        return (null, null);
                    }
                }
                request.Log($"Some requirements have native code; vendored for {string.Join(", ", targets)}.");

                // The adapter describes itself here, on this machine, which may be none of them.
                string describeOnly = null;
                var here = Runtimes.AdapterRuntimes.CurrentPlatform;
                if (!targets.Contains(here, StringComparer.OrdinalIgnoreCase))
                {
                    describeOnly = Path.Combine(vendor, here);
                    var (ok, output) = await PipAsync(request, new[] { "install", "--no-compile", "--target", describeOnly, "-r", filtered });
                    if (!ok)
                    {
                        result.Problems.Add($"pip could not install the requirements on this machine to describe the adapter: {LastLines(output)}");
                        return (null, null);
                    }
                }
                return (targets, describeOnly);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        static IEnumerable<string> WheelOptions(string platform) =>
            new[] { "--only-binary=:all:", "--python-version", TargetPythonVersion, "--implementation", "cp" }
                .Concat(PipPlatforms[platform].SelectMany(tag => new[] { "--platform", tag }));

        static bool IsOwnPackage(string line)
        {
            var name = Regex.Match(line.Trim(), @"^[A-Za-z0-9_.\-]+").Value;
            return OwnPackages.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        static async Task<(bool Ok, string Output)> PipAsync(BuildRequest request, string[] arguments)
        {
            var start = new ProcessStartInfo(request.Runtimes.PythonExecutable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-m", "pip", "--disable-pip-version-check", "--no-input" }.Concat(arguments))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode == 0, await stdout + await stderr);
        }

        static string LastLines(string output) =>
            string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));

        /// <summary>
        /// The package's entry: puts the vendored code on the path — this platform's folder first,
        /// when requirements were vendored per platform — and runs the adapter's own entry as __main__.
        /// </summary>
        internal static string Bootstrap(string entry) => $$"""
            # Written by serverless build. Puts the vendored packages on the path and runs {{entry}}.
            import os, platform, runpy, sys

            here = os.path.dirname(os.path.abspath(__file__))
            vendor = os.path.join(here, "{{VendorFolder}}")
            machine = platform.machine().lower()
            arch = "arm64" if machine in ("arm64", "aarch64") else "x64"
            system = {"linux": "linux", "darwin": "osx", "win32": "win"}.get(sys.platform, sys.platform)
            musl = system == "linux" and "musl" in (platform.libc_ver()[0] or "")
            native = os.path.join(vendor, f"{system}-musl-{arch}" if musl else f"{system}-{arch}")
            for folder in (vendor, native):
                if os.path.isdir(folder):
                    sys.path.insert(0, folder)
            sys.path.insert(0, here)
            sys.argv[0] = os.path.join(here, "{{entry}}")
            runpy.run_path(sys.argv[0], run_name="__main__")
            """;
    }
}
