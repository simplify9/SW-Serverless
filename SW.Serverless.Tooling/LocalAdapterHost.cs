using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Resident;
using SW.Serverless.Runtimes;

namespace SW.Serverless.Tooling
{
    /// <summary>
    /// An adapter package run on this machine exactly as a host runs one: put in a temporary store,
    /// installed from it, started on its runtime, classic or resident, and called by command name.
    /// What sw-serverless run and sw-serverless test use.
    /// </summary>
    public sealed class LocalAdapterHost : IAsyncDisposable
    {
        const string Version = "0.0.0";

        readonly IHost host;
        readonly ISession session;
        readonly string work;
        readonly bool ownsWork;

        LocalAdapterHost(IHost host, ISession session, string work, bool ownsWork)
        {
            this.host = host;
            this.session = session;
            this.work = work;
            this.ownsWork = ownsWork;
        }

        /// <summary>As the overload with limits, with none: kept so code built against 10.2.0 still binds.</summary>
        public static Task<LocalAdapterHost> StartAsync(string packageDirectory, IDictionary<string, string> settings,
            AdapterRuntimeOptions runtimes, int commandTimeoutSeconds, string workDirectory) =>
            StartAsync(packageDirectory, settings, runtimes, commandTimeoutSeconds, workDirectory, limits: null);

        /// <summary>
        /// Installs the package in a temporary store and starts it. With <paramref name="limits"/>, the
        /// adapter runs under those memory and CPU ceilings, sampled every
        /// <see cref="LocalAdapterLimits.SampleInterval"/>; a call it is running when it crosses one
        /// fails with an <see cref="AdapterStoppedException"/> naming the limit. Without, it runs as
        /// a host runs it by default, unconstrained.
        /// </summary>
        public static async Task<LocalAdapterHost> StartAsync(string packageDirectory, IDictionary<string, string> settings,
            AdapterRuntimeOptions runtimes = null, int commandTimeoutSeconds = 60, string workDirectory = null,
            LocalAdapterLimits limits = null)
        {
            if (limits is { IsEmpty: true }) limits = null;
            var manifest = AdapterManifest.Parse(File.ReadAllText(Path.Combine(packageDirectory, AdapterManifest.FileName)));
            var work = workDirectory ?? Path.Combine(Path.GetTempPath(), "swsl-local", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            var host = Build(runtimes ?? new AdapterRuntimeOptions(), commandTimeoutSeconds, work, limits);
            await host.StartAsync();
            try
            {
                var adapterId = string.IsNullOrWhiteSpace(manifest.Id) ? "local.adapter" : manifest.Id;
                await UploadAsync(host.Services.GetRequiredService<ICloudFilesService>(), packageDirectory, adapterId,
                    manifest.EntryFor(AdapterRuntimes.CurrentPlatform));

                var adapterRef = $"{adapterId}/{Version}";
                ISession session = manifest.IsResident
                    ? await ResidentSession.StartAsync(host.Services, adapterRef, settings, commandTimeoutSeconds)
                    : await ClassicSession.StartAsync(host.Services, adapterRef, settings, commandTimeoutSeconds);
                return new LocalAdapterHost(host, session, work, workDirectory == null)
                {
                    limits = limits,
                    adapterId = adapterRef,
                    // The process the calls go to, when the resident host runs it: every adapter but
                    // a .NET one on the classic text protocol, which no limit reaches.
                    instance = host.Services.GetRequiredService<IResidentAdapterHost>().List().FirstOrDefault(),
                };
            }
            catch
            {
                await host.StopAsync();
                host.Dispose();
                if (workDirectory == null) try { Directory.Delete(work, true); } catch { }
                throw;
            }
        }

        LocalAdapterLimits limits;
        string adapterId;
        ResidentAdapterInstance instance;

        /// <summary>Calls a command and returns its answer as text: raw for a string, JSON for anything else.</summary>
        public async Task<string> CallAsync(string command, object input)
        {
            try
            {
                return await session.CallAsync(command, input);
            }
            catch (Exception ex) when (limits != null)
            {
                var explained = await ExplainAsync(ex);
                if (explained == null) throw;
                throw explained;
            }
        }

        /// <summary>Calls a command that answers nothing.</summary>
        public async Task CallVoidAsync(string command, object input)
        {
            try
            {
                await session.CallVoidAsync(command, input);
            }
            catch (Exception ex) when (limits != null)
            {
                var explained = await ExplainAsync(ex);
                if (explained == null) throw;
                throw explained;
            }
        }

        static readonly string[] OutOfMemorySigns =
        {
            "heap out of memory", "Reached heap limit", "MemoryError", "OutOfMemoryException", "Cannot allocate memory",
        };

        /// <summary>
        /// A failure that a limit explains, said in those terms; null to let it through as it is.
        /// The host's own stops already say which limit, and a runtime's own ceiling raises inside
        /// the adapter — but an adapter that died at its runtime's ceiling (Node's heap) only closed
        /// its stream, which reads as nothing in particular.
        /// </summary>
        async Task<Exception> ExplainAsync(Exception ex)
        {
            if (ex is AdapterStoppedException || limits.MemoryLimitBytes <= 0) return null;
            var limit = $"{limits.MemoryLimitBytes / 1024 / 1024} MB";

            if (ex is AdapterInvocationException invocation)
            {
                var outOfMemory = (invocation.AdapterExceptionType ?? "").Contains("OutOfMemory") ||
                                  (invocation.AdapterExceptionType ?? "").EndsWith("MemoryError");
                if (!outOfMemory || invocation.Message.Contains("memory limit")) return null;
                var message = invocation.Message[(invocation.AdapterExceptionType.Length + 2)..];
                return new AdapterInvocationException(invocation.AdapterExceptionType,
                    $"the adapter ran out of memory: it runs under a memory limit of {limit}" +
                    (string.IsNullOrWhiteSpace(message) ? "" : $" ({message})"), invocation.Detail);
            }

            if (ex is not IOException || instance?.Process == null) return null;

            // Its last words are on stderr, read as they come: wait for the process to be gone.
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await instance.Process.WaitForExitAsync(wait.Token);
            }
            catch { /* still running, or already disposed */ }

            if (instance.StopReason != null)
                return new AdapterStoppedException(adapterId, instance.StopReason, instance.StoppedForLimit);
            var sign = instance.Diagnostics.Select(line => OutOfMemorySigns.FirstOrDefault(s =>
                    line.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(s => s != null);
            return sign == null
                ? null
                : new AdapterStoppedException(adapterId,
                    $"it ran out of memory under its memory limit of {limit} and exited ({sign})", limitExceeded: true);
        }

        public async ValueTask DisposeAsync()
        {
            try { await session.DisposeAsync(); } catch { /* stopping only */ }
            await host.StopAsync();
            host.Dispose();
            if (ownsWork) try { Directory.Delete(work, true); } catch { }
        }

        /// <summary>The default host tolerates 45 seconds of missed heartbeats; sampling faster keeps that.</summary>
        static readonly TimeSpan HeartbeatTolerance = TimeSpan.FromSeconds(45);

        static IHost Build(AdapterRuntimeOptions runtimes, int commandTimeoutSeconds, string work, LocalAdapterLimits limits)
        {
            var tag = Guid.NewGuid().ToString("N")[..10];
            return Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureAppConfiguration(c => c.Sources.Clear())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o =>
                    {
                        o.BucketName = "local-" + tag;
                        o.StoragePath = Path.Combine(work, "store");
                    });
                    s.AddAdapterRuntimes(o =>
                    {
                        o.PythonExecutable = runtimes.PythonExecutable;
                        o.NodeExecutable = runtimes.NodeExecutable;
                        o.DotnetExecutable = runtimes.DotnetExecutable;
                    });
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(work, "installed");
                        o.AdapterMetadataCacheDuration = 1;
                        o.CommandTimeout = commandTimeoutSeconds;
                    });
                    s.AddResidentAdapters<NoEvents>(o =>
                    {
                        // Unix socket paths are short-limited, so under /tmp rather than the work folder.
                        o.SocketPath = Path.Combine("/tmp", $"swsl-local-{tag}.sock");
                        o.PipeName = $"swsl-local-{tag}";
                        o.HandshakeTimeout = TimeSpan.FromSeconds(60);
                        if (limits == null) return;

                        // Sampled on the heartbeat, so the heartbeat is the sample interval: at the
                        // default 15 seconds a short try finishes between two samples. The ping waits
                        // as long as the interval, so the misses allowed grow with it and an adapter
                        // busy in one long call is given the 45 seconds it always was.
                        var interval = limits.SampleInterval > TimeSpan.FromMilliseconds(250)
                            ? limits.SampleInterval : TimeSpan.FromMilliseconds(250);
                        o.HeartbeatInterval = interval;
                        o.MissedHeartbeatsBeforeRestart = Math.Max(3, (int)Math.Ceiling(HeartbeatTolerance / interval));
                        o.HardMemoryLimitBytes = Math.Max(0, limits.MemoryLimitBytes);
                        o.CpuPercentLimit = Math.Max(0, limits.CpuPercentLimit);
                        o.CpuLimitSamples = Math.Max(1, limits.CpuLimitSamples);
                        // Asked to stop at the CPU ceiling, an adapter in a runaway loop never will.
                        o.DrainDeadline = interval * 2 > TimeSpan.FromSeconds(2) ? interval * 2 : TimeSpan.FromSeconds(2);
                    });
                })
                .Build();
        }

        static async Task UploadAsync(ICloudFilesService files, string directory, string adapterId, string entry)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(directory, file).Replace('\\', '/'), CompressionLevel.Fastest);

            buffer.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(buffer))[..16].ToLowerInvariant();
            buffer.Position = 0;
            await files.WriteAsync(buffer, new WriteFileSettings
            {
                Key = AdapterCatalogPaths.Version("adapters", adapterId, Version),
                ContentType = "application/zip",
                Metadata = new Dictionary<string, string> { ["EntryAssembly"] = entry, ["Hash"] = "local-" + hash },
            });
        }

        interface ISession : IAsyncDisposable
        {
            Task<string> CallAsync(string command, object input);
            Task CallVoidAsync(string command, object input);
        }

        sealed class ClassicSession(IServiceScope scope, IServerlessService service, int timeout) : ISession
        {
            public static async Task<ISession> StartAsync(IServiceProvider services, string adapterRef,
                IDictionary<string, string> settings, int timeout)
            {
                var scope = services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IServerlessService>();
                try
                {
                    await service.StartAsync(adapterRef, "local", new Dictionary<string, string>(settings ?? new Dictionary<string, string>()));
                }
                catch
                {
                    (service as IDisposable)?.Dispose();
                    scope.Dispose();
                    throw;
                }
                return new ClassicSession(scope, service, timeout);
            }

            public Task<string> CallAsync(string command, object input) => service.InvokeAsync<string>(command, input, timeout);
            public Task CallVoidAsync(string command, object input) => service.InvokeAsync(command, input, timeout);

            public ValueTask DisposeAsync()
            {
                (service as IDisposable)?.Dispose();
                scope.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        sealed class ResidentSession(IResidentAdapterHost host, ResidentAdapterInstance instance, string adapterRef, int timeout) : ISession
        {
            public static async Task<ISession> StartAsync(IServiceProvider services, string adapterRef,
                IDictionary<string, string> settings, int timeout)
            {
                var host = services.GetRequiredService<IResidentAdapterHost>();
                var instance = await host.StartExclusiveAsync(new AdapterSpec
                {
                    AdapterId = adapterRef,
                    InstanceKey = "local",
                    StartupValues = new Dictionary<string, string>(settings ?? new Dictionary<string, string>()),
                });
                return new ResidentSession(host, instance, adapterRef, timeout);
            }

            public Task<string> CallAsync(string command, object input) => instance.InvokeAsync<string>(command, input, timeoutSeconds: timeout);
            public Task CallVoidAsync(string command, object input) => instance.InvokeAsync<object>(command, input, timeoutSeconds: timeout);

            public async ValueTask DisposeAsync() => await host.StopAsync(adapterRef, "local", drain: false);
        }

        sealed class NoEvents : IAdapterEventSink
        {
            public Task<EventOutcome> OnEventAsync(InboundEvent inboundEvent, CancellationToken cancellationToken) =>
                Task.FromResult(EventOutcome.Ok("local"));
        }

        /// <summary>Runs an adapter with --describe and reads its answer; null and a reason when it gives none.</summary>
        public static async Task<(AdapterSelfDescription Description, string Problem)> DescribeAsync(string entryPath,
            string runtime, AdapterRuntimeOptions runtimes = null, int timeoutSeconds = 30)
        {
            var launcher = new AdapterRuntimes(runtimes).Find(runtime);
            if (launcher == null) return (null, $"no launcher for the '{runtime}' runtime");

            var startInfo = launcher.StartInfo(entryPath, new RuntimeLaunch { Arguments = new[] { AdapterSelfDescription.Flag } });
            try
            {
                using var process = Process.Start(startInfo)!;
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutSeconds * 1000))
                {
                    try { process.Kill(true); } catch { }
                    return (null, $"it didn't answer {AdapterSelfDescription.Flag} within {timeoutSeconds} seconds — an SDK older than 10.1.0 doesn't know it");
                }
                if (process.ExitCode != 0)
                    return (null, $"{AdapterSelfDescription.Flag} exited with {process.ExitCode}: {await errors}");
                return (AdapterSelfDescription.Parse(await output), null);
            }
            catch (Exception ex)
            {
                return (null, $"its description couldn't be read: {ex.Message}");
            }
        }
    }
}
