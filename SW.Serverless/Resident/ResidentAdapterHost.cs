using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    public class ResidentAdapterHost : IResidentAdapterHost, IHostedService, IAsyncDisposable
    {
        readonly ResidentOptions options;
        readonly IAdapterEventSink sink;
        readonly ILoggerFactory loggerFactory;
        readonly ILogger<ResidentAdapterHost> logger;
        readonly ResidentAdapterRegistry registry = new();
        readonly AdapterProcessLauncher launcher;

        readonly ConcurrentDictionary<string, Supervised> instances = new();
        readonly ConcurrentDictionary<string, AdapterPool> pools = new();

        // Exclusive means exclusive. Two concurrent starts for one key — the supervisor and a
        // manual start, say — would otherwise both find no Ready instance and both spawn, and for
        // a broker connection that means duplicate consumption.
        readonly ConcurrentDictionary<string, SemaphoreSlim> startGates = new();
        readonly CancellationTokenSource stopping = new();

        IHost transport;
        Task supervisorTask;
        int stopped;

        readonly IResidentAdapterLocator locator;

        public ResidentAdapterHost(ResidentOptions options, IAdapterEventSink sink,
            IResidentAdapterLocator locator, ILoggerFactory loggerFactory)
        {
            this.options = options;
            this.sink = sink;
            this.locator = locator;
            this.loggerFactory = loggerFactory;
            logger = loggerFactory.CreateLogger<ResidentAdapterHost>();
            launcher = new AdapterProcessLauncher(options, logger);
        }

        // ------------------------------------------------------------------ transport

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            if (!isWindows && File.Exists(options.SocketPath))
                File.Delete(options.SocketPath);

            transport = new HostBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseKestrel(k =>
                    {
                        if (isWindows)
                            k.ListenNamedPipe(options.PipeName, l => l.Protocols = HttpProtocols.Http2);
                        else
                            k.ListenUnixSocket(options.SocketPath, l => l.Protocols = HttpProtocols.Http2);
                    })
                    .ConfigureServices(s =>
                    {
                        s.AddGrpc(o =>
                        {
                            o.MaxReceiveMessageSize = 64 * 1024 * 1024;
                            o.MaxSendMessageSize = 64 * 1024 * 1024;
                        });
                        s.AddSingleton(registry);
                        s.AddSingleton(loggerFactory);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapGrpcService<AdapterHostService>());
                    }))
                .Build();

            if (!isWindows)
            {
                // Tighten the DIRECTORY before the socket exists, so there is no window in which
                // the endpoint is reachable with default permissions. The socket file itself is
                // still narrowed below for platforms that honour its mode.
                var directory = Path.GetDirectoryName(Path.GetFullPath(options.SocketPath));
                if (!string.IsNullOrEmpty(directory) && directory != "/tmp")
                {
                    Directory.CreateDirectory(directory);
                    try
                    {
                        File.SetUnixFileMode(directory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not tighten the socket directory."); }
                }
            }

            await transport.StartAsync(cancellationToken);

            if (!isWindows)
            {
                try { File.SetUnixFileMode(options.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not tighten socket permissions."); }
            }

            logger.LogInformation("Resident adapter host listening on {Endpoint}.",
                isWindows ? @"\\.\pipe\" + options.PipeName : options.SocketPath);

            supervisorTask = Task.Run(() => SuperviseAsync(stopping.Token));
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref stopped, 1) == 1) return;

            try { stopping.Cancel(); } catch (ObjectDisposedException) { }

            // Bounded by the host's own deadline: a hung adapter must not hold up shutdown for
            // 30 seconds each, in sequence, until the orchestrator loses patience and SIGKILLs us.
            await Task.WhenAny(
                Task.WhenAll(instances.Values.ToArray().Select(s => StopSupervisedAsync(s, drain: true))),
                Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None));

            foreach (var pool in pools.Values) await pool.DisposeAsync();

            if (supervisorTask != null)
                await Task.WhenAny(supervisorTask, Task.Delay(5000, CancellationToken.None));

            if (transport != null) await transport.StopAsync(cancellationToken);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(options.SocketPath))
                try { File.Delete(options.SocketPath); } catch { }
        }

        // ------------------------------------------------------------------ exclusive

        public async Task<ResidentAdapterInstance> StartExclusiveAsync(AdapterSpec spec, CancellationToken cancellationToken = default)
        {
            var key = Key(spec.AdapterId, spec.InstanceKey);
            var gate = startGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (instances.TryGetValue(key, out var existing) &&
                    existing.Instance?.State == InstanceState.Ready)
                    return existing.Instance;

                var supervised = new Supervised { Spec = spec };
                instances[key] = supervised;
                await SpawnAsync(supervised, cancellationToken);
                return supervised.Instance;
            }
            finally
            {
                gate.Release();
            }
        }

        async Task SpawnAsync(Supervised supervised, CancellationToken cancellationToken)
        {
            var spec = supervised.Spec;

            // Resolves an explicit path, or installs from cloud storage — which is what makes
            // "add a provider without redeploying" real (design doc 15.2).
            var resolved = await locator.ResolveAsync(spec, cancellationToken);
            spec.EntryAssemblyPath = resolved.EntryAssemblyPath;
            spec.Executable = resolved.Executable ?? spec.Executable;
            if (resolved.AdapterValues != null) spec.AdapterValues = resolved.AdapterValues;

            var instance = new ResidentAdapterInstance(
                spec.AdapterId,
                spec.InstanceKey ?? "default",
                Guid.NewGuid().ToString("N"),
                options, sink, loggerFactory)
            {
                StartupValues = new Dictionary<string, string>(
                    spec.StartupValues ?? new Dictionary<string, string>()),
                AdapterValues = new Dictionary<string, string>(
                    spec.AdapterValues ?? new Dictionary<string, string>()),
                RestartCount = supervised.RestartCount
            };

            supervised.Instance = instance;
            registry.Expect(instance);

            try
            {
                instance.Process = launcher.Launch(spec, instance);
            }
            catch
            {
                // Otherwise the registry keeps waiting for a child that will never attach.
                registry.Forget(instance.Token);
                throw;
            }

            instance.Process.Exited += (_, _) => OnExited(supervised, instance);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
            timeout.CancelAfter(options.HandshakeTimeout);

            try
            {
                await instance.Attached.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                registry.Forget(instance.Token);
                var tail = string.Join("\n", instance.Diagnostics.TakeLast(20));
                TryKill(instance.Process);
                throw new TimeoutException(
                    $"Adapter '{spec.AdapterId}' did not attach within {options.HandshakeTimeout}." +
                    (string.IsNullOrWhiteSpace(tail) ? "" : $" Last output:\n{tail}"));
            }
        }

        void OnExited(Supervised supervised, ResidentAdapterInstance instance)
        {
            if (stopping.IsCancellationRequested || supervised.Stopping) return;
            if (!ReferenceEquals(supervised.Instance, instance)) return;

            var code = SafeExitCode(instance.Process);
            logger.LogWarning("Adapter {AdapterId}/{InstanceKey} exited with code {Code}. Last output:\n{Tail}",
                instance.AdapterId, instance.InstanceKey, code,
                string.Join("\n", instance.Diagnostics.TakeLast(20)));

            supervised.RecordCrash(options);
            supervised.MissedHeartbeats = 0;
            supervised.DrainRequested = false;
            supervised.LastCpuSampleOn = null;
            supervised.CpuOverSamples = 0;
            _ = instance.DisposeAsync().AsTask();

            if (supervised.Quarantined)
            {
                instance.MarkQuarantined();
                logger.LogError(
                    "Adapter {AdapterId}/{InstanceKey} crash-looped {Count} times in {Window}. Quarantined — a silent restart loop is worse than a hard stop.",
                    instance.AdapterId, instance.InstanceKey, supervised.RestartCount, options.CrashLoopWindow);
                return;
            }

            _ = Task.Run(async () =>
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, supervised.RestartCount))));
                try
                {
                    await Task.Delay(delay, stopping.Token);
                    await SpawnAsync(supervised, stopping.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Restart of {AdapterId} failed.", instance.AdapterId);
                }
            });
        }

        public Task<LimitUpdate> UpdateLimitsAsync(string adapterId, string instanceKey,
            ResourceLimits limits, CancellationToken cancellationToken = default)
        {
            if (limits == null) throw new ArgumentNullException(nameof(limits));

            if (!instances.TryGetValue(Key(adapterId, instanceKey), out var supervised))
                return Task.FromResult(new LimitUpdate { Applied = false, Reason = "No such instance." });

            var spec = supervised.Spec;
            var hardChanged = limits.HardMemoryLimitBytes != spec.HardMemoryLimitBytes;

            spec.SoftMemoryLimitBytes = limits.SoftMemoryLimitBytes;
            spec.HardMemoryLimitBytes = limits.HardMemoryLimitBytes;
            spec.CpuPercentLimit = limits.CpuPercentLimit;
            spec.CpuLimitSamples = limits.CpuLimitSamples;

            // A lowered CPU ceiling should not trip on a streak the adapter accumulated under the
            // old one — that would recycle it for something it did before the rule existed.
            supervised.CpuOverSamples = 0;

            logger.LogInformation("Adapter {AdapterId}/{InstanceKey} limits set to {Limits}.",
                adapterId, instanceKey, limits);

            return Task.FromResult(new LimitUpdate
            {
                Applied = true,
                Limits = limits,
                // The soft and CPU ceilings are read from the spec on every sample, so they are
                // already in force. The hard one was handed to the runtime as a GC heap limit when
                // the process launched, and nothing can change that in place.
                RestartRequired = hardChanged && limits.HardMemoryLimitBytes > 0,
                Reason = hardChanged && limits.HardMemoryLimitBytes > 0
                    ? "The hard memory ceiling is the runtime's own GC heap limit, set when the "
                      + "process launched. It applies from the next restart."
                    : null,
            });
        }

        public async Task<ResidentAdapterInstance> RestartAsync(string adapterId, string instanceKey,
            bool drain = true, CancellationToken cancellationToken = default)
        {
            if (!instances.TryGetValue(Key(adapterId, instanceKey), out var supervised))
                throw new InvalidOperationException(
                    $"No resident adapter '{adapterId}' with instance key '{instanceKey}' is running here.");

            // Relaunch in place: the entry stays in the registry under the same key, so whatever
            // owns this instance — a lease, a data source, a caller holding the key — still points
            // at it afterwards. Stopping and starting instead would drop the entry and, in
            // Bitween's case, release the broker lease that makes the connection exclusive.
            //
            // Stopping is set for the teardown so the exit does not look like a crash and trigger
            // the backoff restart; this method does the relaunch itself.
            supervised.Stopping = true;
            try
            {
                var old = supervised.Instance;
                if (old != null)
                {
                    old.RequestShutdown("restart requested", drain);
                    var deadline = drain ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5);
                    if (old.Process != null && !old.Process.HasExited)
                        await Task.Run(() => old.Process.WaitForExit((int)deadline.TotalMilliseconds),
                            cancellationToken);

                    TryKill(old.Process);
                    await old.DisposeAsync();
                }

                supervised.MissedHeartbeats = 0;
                supervised.DrainRequested = false;
                supervised.LastCpuSampleOn = null;
                supervised.CpuOverSamples = 0;
            }
            finally
            {
                supervised.Stopping = false;
            }

            await SpawnAsync(supervised, cancellationToken);

            logger.LogInformation("Adapter {AdapterId}/{InstanceKey} restarted on request.",
                adapterId, instanceKey);

            return supervised.Instance;
        }

        public async Task StopAsync(string adapterId, string instanceKey, bool drain = true,
            CancellationToken cancellationToken = default)
        {
            if (instances.TryRemove(Key(adapterId, instanceKey), out var supervised))
                await StopSupervisedAsync(supervised, drain);
        }

        async Task StopSupervisedAsync(Supervised supervised, bool drain)
        {
            supervised.Stopping = true;
            var instance = supervised.Instance;
            if (instance == null) return;

            try
            {
                instance.RequestShutdown("host stopping", drain);
                var deadline = drain ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5);
                if (instance.Process != null && !instance.Process.HasExited)
                    await Task.Run(() => instance.Process.WaitForExit((int)deadline.TotalMilliseconds));
            }
            catch { /* teardown */ }
            finally
            {
                TryKill(instance.Process);
                await instance.DisposeAsync();
            }
        }

        // ------------------------------------------------------------------ pooled

        public Task<IAdapterLease> RentAsync(AdapterSpec spec, CancellationToken cancellationToken = default)
        {
            var pool = pools.GetOrAdd(spec.AdapterId,
                _ => new AdapterPool(spec, this, options, loggerFactory.CreateLogger<AdapterPool>()));
            return pool.RentAsync(cancellationToken);
        }

        internal async Task<ResidentAdapterInstance> SpawnPooledAsync(AdapterSpec spec, string slot,
            CancellationToken ct)
        {
            var supervised = new Supervised { Spec = CloneWithKey(spec, slot) };
            instances[Key(spec.AdapterId, slot)] = supervised;

            // ContinueWith swallowed the failure and handed back a null instance, so the caller
            // got a NullReferenceException instead of the reason the spawn failed.
            await SpawnAsync(supervised, ct);
            return supervised.Instance;
        }

        internal Task RetireAsync(string adapterId, string slot) =>
            StopAsync(adapterId, slot, drain: false);

        static AdapterSpec CloneWithKey(AdapterSpec spec, string key) => new()
        {
            AdapterId = spec.AdapterId,
            InstanceKey = key,
            EntryAssemblyPath = spec.EntryAssemblyPath,
            Executable = spec.Executable,
            StartupValues = spec.StartupValues,
            AdapterValues = spec.AdapterValues,
            SoftMemoryLimitBytes = spec.SoftMemoryLimitBytes,
            HardMemoryLimitBytes = spec.HardMemoryLimitBytes,
            // Easy to miss, and silent when missed: a pooled adapter would run with the memory
            // ceilings its spec asked for and no CPU ceiling at all.
            CpuPercentLimit = spec.CpuPercentLimit,
            CpuLimitSamples = spec.CpuLimitSamples
        };

        // ------------------------------------------------------------------ lookups

        public ResidentAdapterInstance Get(string adapterId, string instanceKey) =>
            instances.TryGetValue(Key(adapterId, instanceKey), out var s) ? s.Instance : null;

        public IReadOnlyCollection<ResidentAdapterInstance> List() =>
            instances.Values.Select(v => v.Instance).Where(i => i != null).ToArray();

        public IReadOnlyCollection<InstanceHealth> Describe() =>
            instances.Values.Where(v => v.Instance != null).Select(Describe).ToArray();

        static InstanceHealth Describe(Supervised supervised)
        {
            var instance = supervised.Instance;
            var status = instance.LastStatus;

            var health = new InstanceHealth
            {
                AdapterId = instance.AdapterId,
                InstanceKey = instance.InstanceKey,
                State = instance.State,
                WorkingSetBytes = supervised.LastWorkingSet,
                CpuPercent = supervised.CpuPercent,
                ThreadCount = supervised.LastThreadCount,
                Uptime = DateTimeOffset.UtcNow - instance.StartedOn,
                RestartCount = supervised.RestartCount,
                MissedHeartbeats = supervised.MissedHeartbeats,
                Quarantined = supervised.Quarantined,
                DrainRequested = supervised.DrainRequested,
                LastHeartbeatOn = supervised.LastHeartbeatOn,
                IdleSince = instance.IdleSince,
                Capabilities = instance.Capabilities,
                Commands = instance.Commands,
                CommandDetails = instance.CommandDetails,
                SdkVersion = instance.SdkVersion,
                ProtocolVersion = instance.ProtocolVersion,
                StartupValues = instance.StartupValues,
                Diagnostics = instance.Diagnostics
            };

            try { health.ProcessId = instance.Process?.HasExited == false ? instance.Process.Id : null; }
            catch { }

            if (status != null)
            {
                health.Connected = status.Connected;
                health.ReportedState = status.State;
                health.InFlight = status.InFlight;
                health.LastError = string.IsNullOrEmpty(status.LastError) ? null : status.LastError;
                if (status.LastMessageUnixMs > 0)
                    health.LastMessageOn = DateTimeOffset.FromUnixTimeMilliseconds(status.LastMessageUnixMs);
                foreach (var kv in status.Details) health.Details[kv.Key] = kv.Value;
            }

            return health;
        }

        static string Key(string adapterId, string instanceKey) =>
            $"{adapterId}::{instanceKey ?? "default"}".ToLowerInvariant();

        // ------------------------------------------------------------------ supervisor

        async Task SuperviseAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(options.HeartbeatInterval, ct); }
                catch (OperationCanceledException) { return; }

                // Concurrently: sequential pings meant one wedged adapter delayed detection for
                // every adapter behind it in the loop, so a whole node could look healthy because
                // the first instance was hanging.
                await Task.WhenAll(instances.Values.ToArray().Select(HeartbeatAsync));
                await Task.WhenAll(pools.Values.ToArray().Select(p => p.EvictIdleAsync()));
            }
        }

        async Task HeartbeatAsync(Supervised supervised)
        {
            var instance = supervised.Instance;
            if (instance == null || instance.State != InstanceState.Ready) return;

            // Host-observed metrics need no adapter cooperation, so they still work when the
            // adapter is wedged (design doc 6.3).
            SampleProcess(supervised, instance);

            try
            {
                await instance.PingAsync(options.HeartbeatInterval);
                supervised.MissedHeartbeats = 0;
                supervised.LastHeartbeatOn = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                supervised.MissedHeartbeats++;
                logger.LogWarning(ex, "Heartbeat {Missed}/{Max} missed for {AdapterId}/{InstanceKey}.",
                    supervised.MissedHeartbeats, options.MissedHeartbeatsBeforeRestart,
                    instance.AdapterId, instance.InstanceKey);

                if (supervised.MissedHeartbeats >= options.MissedHeartbeatsBeforeRestart)
                {
                    supervised.MissedHeartbeats = 0;
                    TryKill(instance.Process);   // Exited handler restarts it
                }
            }
        }

        void SampleProcess(Supervised supervised, ResidentAdapterInstance instance)
        {
            try
            {
                var process = instance.Process;
                if (process == null || process.HasExited) return;
                process.Refresh();

                var rss = process.WorkingSet64;
                supervised.LastWorkingSet = rss;
                supervised.LastThreadCount = process.Threads.Count;

                var cpu = process.TotalProcessorTime;
                var now = DateTimeOffset.UtcNow;
                if (supervised.LastCpuSampleOn.HasValue)
                {
                    var wall = (now - supervised.LastCpuSampleOn.Value).TotalMilliseconds;
                    if (wall > 0)
                        supervised.CpuPercent = Math.Round(
                            (cpu - supervised.LastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100, 1);
                }
                supervised.LastCpu = cpu;
                supervised.LastCpuSampleOn = now;

                var soft = supervised.Spec.SoftMemoryLimitBytes > 0
                    ? supervised.Spec.SoftMemoryLimitBytes : options.SoftMemoryLimitBytes;
                var hard = supervised.Spec.HardMemoryLimitBytes > 0
                    ? supervised.Spec.HardMemoryLimitBytes : options.HardMemoryLimitBytes;
                var cpuLimit = supervised.Spec.CpuPercentLimit > 0
                    ? supervised.Spec.CpuPercentLimit : options.CpuPercentLimit;
                var cpuSamples = supervised.Spec.CpuLimitSamples > 0
                    ? supervised.Spec.CpuLimitSamples : Math.Max(1, options.CpuLimitSamples);

                // CPU is judged over consecutive samples, never on one. An adapter draining a
                // backlog is supposed to peg a core; only a run of samples separates that from a
                // loop that will never stop. The first sample after a launch has no elapsed wall
                // time behind it, so it is not counted.
                if (cpuLimit > 0 && supervised.CpuPercent > 0)
                {
                    if (supervised.CpuPercent > cpuLimit) supervised.CpuOverSamples++;
                    else supervised.CpuOverSamples = 0;

                    if (supervised.CpuOverSamples >= cpuSamples && !supervised.DrainRequested)
                    {
                        supervised.DrainRequested = true;
                        supervised.CpuOverSamples = 0;
                        logger.LogWarning(
                            "Adapter {AdapterId}/{InstanceKey} held {Cpu}% CPU across {Samples} samples (limit {Limit}%); asking it to drain.",
                            instance.AdapterId, instance.InstanceKey, supervised.CpuPercent, cpuSamples, cpuLimit);
                        instance.RequestShutdown("sustained cpu limit", drain: true);
                        return;
                    }
                }

                // Soft first: draining lets in-flight messages be nacked. A hard kill cannot.
                if (soft > 0 && rss > soft && !supervised.DrainRequested)
                {
                    supervised.DrainRequested = true;
                    logger.LogWarning("Adapter {AdapterId}/{InstanceKey} at {Rss} MB crossed the soft limit; asking it to drain.",
                        instance.AdapterId, instance.InstanceKey, rss / 1024 / 1024);
                    instance.RequestShutdown("soft memory limit", drain: true);
                }
                else if (hard > 0 && rss > hard)
                {
                    logger.LogError("Adapter {AdapterId}/{InstanceKey} at {Rss} MB crossed the hard limit; killing.",
                        instance.AdapterId, instance.InstanceKey, rss / 1024 / 1024);
                    TryKill(process);
                }
            }
            catch { /* sampling must never throw */ }
        }

        static int SafeExitCode(Process p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }
        static void TryKill(Process p) { try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { } }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None);
            try { stopping.Dispose(); } catch (ObjectDisposedException) { }
            transport?.Dispose();
        }

        internal class Supervised
        {
            public AdapterSpec Spec;
            public ResidentAdapterInstance Instance;
            public int RestartCount;
            public int MissedHeartbeats;
            public bool Stopping;
            public bool DrainRequested;
            public bool Quarantined;
            public long LastWorkingSet;
            public int LastThreadCount;
            public double CpuPercent;
            public TimeSpan LastCpu;

            /// <summary>Consecutive samples above the CPU ceiling. Reset by any sample under it.</summary>
            public int CpuOverSamples;
            public DateTimeOffset? LastCpuSampleOn;
            public DateTimeOffset? LastHeartbeatOn;
            readonly List<DateTimeOffset> crashes = new();

            public void RecordCrash(ResidentOptions options)
            {
                RestartCount++;
                var now = DateTimeOffset.UtcNow;
                crashes.Add(now);
                crashes.RemoveAll(c => now - c > options.CrashLoopWindow);
                if (crashes.Count >= options.CrashLoopThreshold) Quarantined = true;
            }
        }
    }
}
