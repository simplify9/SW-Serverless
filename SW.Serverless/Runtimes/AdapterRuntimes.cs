using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SW.Serverless.Contract.Catalog;

namespace SW.Serverless.Runtimes
{
    /// <summary>Where the host finds the interpreters its adapters run on.</summary>
    public class AdapterRuntimeOptions
    {
        /// <summary>The Python interpreter. "python3" on the PATH unless set.</summary>
        public string PythonExecutable { get; set; } = "python3";

        /// <summary>The Node.js executable. "node" on the PATH unless set.</summary>
        public string NodeExecutable { get; set; } = "node";

        /// <summary>The .NET host. "dotnet" on the PATH unless set.</summary>
        public string DotnetExecutable { get; set; } = "dotnet";
    }

    /// <summary>Whether a runtime is present on this host, and which version.</summary>
    public sealed record RuntimeStatus(bool Available, string Version, string Reason)
    {
        public static RuntimeStatus Present(string version) => new(true, version, null);
        public static RuntimeStatus Missing(string reason) => new(false, null, reason);
    }

    /// <summary>What a launch is asked to apply, beside the entry it starts.</summary>
    public sealed class RuntimeLaunch
    {
        /// <summary>A memory ceiling the runtime enforces itself, where it can. 0 for none.</summary>
        public long HardMemoryLimitBytes { get; set; }

        /// <summary>.NET only: workstation GC, the right trade for an adapter that mostly waits.</summary>
        public bool UseWorkstationGc { get; set; }

        /// <summary>Arguments after the entry, such as --describe.</summary>
        public System.Collections.Generic.IReadOnlyList<string> Arguments { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// One runtime adapters can be written for: how to tell it's here, and how to start an adapter
    /// on it. Named in an adapter's manifest; never a path a package can choose.
    /// </summary>
    public interface IAdapterRuntime
    {
        /// <summary>As manifests name it: dotnet, exec, python, node.</summary>
        string Name { get; }

        Task<RuntimeStatus> DetectAsync();

        /// <summary>How to start <paramref name="entryPath"/> on this runtime, with its stdio redirected.</summary>
        ProcessStartInfo StartInfo(string entryPath, RuntimeLaunch launch);
    }

    /// <summary>
    /// The runtimes this host knows, and which of them it actually has. Detection runs each
    /// runtime's version check once and remembers the answer for the life of the process.
    /// </summary>
    public class AdapterRuntimes
    {
        readonly Dictionary<string, IAdapterRuntime> runtimes;
        readonly ConcurrentDictionary<string, Lazy<Task<RuntimeStatus>>> detected = new(StringComparer.OrdinalIgnoreCase);

        public AdapterRuntimes(AdapterRuntimeOptions options)
        {
            options ??= new AdapterRuntimeOptions();
            runtimes = new IAdapterRuntime[]
                {
                    new DotnetRuntime(options.DotnetExecutable),
                    new ExecRuntime(),
                    new PythonRuntime(options.PythonExecutable),
                    new NodeRuntime(options.NodeExecutable),
                }
                .ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> Known => runtimes.Keys;

        /// <summary>The runtime named, or null when this host has no launcher for it.</summary>
        public IAdapterRuntime Find(string name) =>
            runtimes.TryGetValue(string.IsNullOrWhiteSpace(name) ? AdapterManifest.DotnetRuntime : name, out var runtime)
                ? runtime
                : null;

        public Task<RuntimeStatus> StatusAsync(string name)
        {
            var runtime = Find(name);
            if (runtime == null) return Task.FromResult(RuntimeStatus.Missing($"this host has no launcher for '{name}'"));
            return detected.GetOrAdd(runtime.Name, _ => new Lazy<Task<RuntimeStatus>>(runtime.DetectAsync)).Value;
        }

        /// <summary>This host's platform as manifests name platforms: linux-x64, osx-arm64, win-x64…</summary>
        public static string CurrentPlatform
        {
            get
            {
                var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
                    : "linux";
                var arch = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.Arm64 => "arm64",
                    Architecture.X86 => "x86",
                    Architecture.Arm => "arm",
                    var other => other.ToString().ToLowerInvariant(),
                };
                return $"{os}-{arch}";
            }
        }

        /// <summary>Runs <paramref name="executable"/> with <paramref name="arguments"/> and reads the first version in what it prints.</summary>
        internal static async Task<RuntimeStatus> VersionOf(string executable, string arguments)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(executable, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process == null) return RuntimeStatus.Missing($"'{executable}' could not be started");
                var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15_000))
                {
                    try { process.Kill(true); } catch { }
                    return RuntimeStatus.Missing($"'{executable} {arguments}' did not answer");
                }
                var version = Regex.Match(output, @"\d+\.\d+(\.\d+)?");
                return process.ExitCode == 0 && version.Success
                    ? RuntimeStatus.Present(version.Value)
                    : RuntimeStatus.Missing($"'{executable} {arguments}' answered: {output.Trim()}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                return RuntimeStatus.Missing($"'{executable}' is not installed on this host");
            }
        }

        static ProcessStartInfo Redirected(string executable, string workingDirectory) => new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        sealed class DotnetRuntime(string executable) : IAdapterRuntime
        {
            public string Name => AdapterManifest.DotnetRuntime;

            // --list-runtimes, not --version: production images carry the runtime without the SDK,
            // and there --version fails. The first line names the oldest runtime installed.
            public Task<RuntimeStatus> DetectAsync() => VersionOf(executable, "--list-runtimes");

            public ProcessStartInfo StartInfo(string entryPath, RuntimeLaunch launch)
            {
                var psi = Redirected(executable, Path.GetDirectoryName(entryPath));
                psi.ArgumentList.Add(entryPath);
                foreach (var argument in launch.Arguments) psi.ArgumentList.Add(argument);
                if (launch.UseWorkstationGc) psi.Environment["DOTNET_gcServer"] = "0";
                if (launch.HardMemoryLimitBytes > 0)
                {
                    psi.Environment["DOTNET_GCHeapHardLimit"] = launch.HardMemoryLimitBytes.ToString("X");
                    psi.Environment["DOTNET_GCConserveMemory"] = "5";
                }
                return psi;
            }
        }

        /// <summary>A self-contained executable: the entry itself is started.</summary>
        sealed class ExecRuntime : IAdapterRuntime
        {
            public string Name => AdapterManifest.ExecRuntime;

            // Nothing to install: the package carries everything it needs.
            public Task<RuntimeStatus> DetectAsync() => Task.FromResult(RuntimeStatus.Present("native"));

            public ProcessStartInfo StartInfo(string entryPath, RuntimeLaunch launch)
            {
                var psi = Redirected(entryPath, Path.GetDirectoryName(entryPath));
                foreach (var argument in launch.Arguments) psi.ArgumentList.Add(argument);
                return psi;
            }
        }

        sealed class PythonRuntime(string executable) : IAdapterRuntime
        {
            public string Name => AdapterManifest.PythonRuntime;

            public Task<RuntimeStatus> DetectAsync() => VersionOf(executable, "--version");

            public ProcessStartInfo StartInfo(string entryPath, RuntimeLaunch launch)
            {
                var psi = Redirected(executable, Path.GetDirectoryName(entryPath));
                // Unbuffered, so what it writes reaches the host's diagnostics when it's written.
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(entryPath);
                foreach (var argument in launch.Arguments) psi.ArgumentList.Add(argument);
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                // Python has no heap ceiling of its own; the watchdog and, where the container
                // allows them, cgroups are what hold it.
                return psi;
            }
        }

        sealed class NodeRuntime(string executable) : IAdapterRuntime
        {
            public string Name => AdapterManifest.NodeRuntime;

            public Task<RuntimeStatus> DetectAsync() => VersionOf(executable, "--version");

            public ProcessStartInfo StartInfo(string entryPath, RuntimeLaunch launch)
            {
                var psi = Redirected(executable, Path.GetDirectoryName(entryPath));
                if (launch.HardMemoryLimitBytes > 0)
                    // V8's own heap ceiling, in megabytes: an allocation past it fails inside the
                    // adapter, as DOTNET_GCHeapHardLimit does for .NET.
                    psi.ArgumentList.Add($"--max-old-space-size={Math.Max(16, launch.HardMemoryLimitBytes / (1024 * 1024))}");
                psi.ArgumentList.Add(entryPath);
                foreach (var argument in launch.Arguments) psi.ArgumentList.Add(argument);
                return psi;
            }
        }
    }
}
