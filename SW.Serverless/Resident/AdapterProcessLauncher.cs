using Microsoft.Extensions.Logging;
using SW.Serverless.Sdk.Resident;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// Spawns an adapter as a local child process and hands it the handshake on stdin.
    /// Runtime tuning is applied through environment variables — design doc 11.1.
    /// </summary>
    internal class AdapterProcessLauncher
    {
        readonly ResidentOptions options;
        readonly ILogger logger;

        public AdapterProcessLauncher(ResidentOptions options, ILogger logger)
        {
            this.options = options;
            this.logger = logger;
        }

        public Process Launch(AdapterSpec spec, ResidentAdapterInstance instance)
        {
            var assembly = spec.EntryAssemblyPath
                           ?? throw new InvalidOperationException(
                               $"Adapter '{spec.AdapterId}' has no EntryAssemblyPath and no locator resolved one.");

            if (!File.Exists(assembly))
                throw new FileNotFoundException($"Adapter entry assembly not found.", assembly);

            var executable = spec.Executable ?? "dotnet";
            var arguments = executable == "dotnet" ? $"\"{assembly}\"" : "";

            var psi = new ProcessStartInfo(executable)
            {
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(assembly),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Workstation GC: server GC allocates a heap and a GC thread per core, which is the
            // wrong trade for an adapter that mostly waits on a socket.
            if (options.UseWorkstationGc) psi.Environment["DOTNET_gcServer"] = "0";

            var hard = spec.HardMemoryLimitBytes > 0 ? spec.HardMemoryLimitBytes : options.HardMemoryLimitBytes;
            if (hard > 0)
            {
                psi.Environment["DOTNET_GCHeapHardLimit"] = hard.ToString("X");
                psi.Environment["DOTNET_GCConserveMemory"] = "5";
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // stderr stays what it is genuinely good at: pre-attach output and crash forensics.
            process.ErrorDataReceived += (_, e) => instance.AddDiagnostic(e.Data);
            process.OutputDataReceived += (_, e) => instance.AddDiagnostic(e.Data);

            if (!process.Start())
                throw new InvalidOperationException($"Failed to start adapter '{spec.AdapterId}'.");

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var handshake = new Handshake
            {
                Token = instance.Token,
                AdapterId = instance.AdapterId,
                InstanceKey = instance.InstanceKey,
                Protocol = 2
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                handshake.Pipe = options.PipeName;
            else
                handshake.Socket = options.SocketPath;

            // The handshake goes on stdin, not argv — nothing secret ends up in `ps aux`.
            process.StandardInput.WriteLine(handshake.Serialize());
            process.StandardInput.Flush();

            ApplyUnixHardening(process, spec);

            logger.LogInformation("Spawned adapter {AdapterId}/{InstanceKey} as pid {Pid}.",
                spec.AdapterId, instance.InstanceKey, process.Id);

            return process;
        }

        /// <summary>
        /// One file write that converts the worst outage mode into a supervised restart: under
        /// memory pressure the kernel kills an adapter rather than the host (design doc 11.2).
        /// </summary>
        static void ApplyUnixHardening(Process process, AdapterSpec spec)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            try
            {
                File.WriteAllText($"/proc/{process.Id}/oom_score_adj", "500");
            }
            catch
            {
                // Not fatal — the watchdog in ResidentAdapterHost still applies.
            }
        }
    }
}
