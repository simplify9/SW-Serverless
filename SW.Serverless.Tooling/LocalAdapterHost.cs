using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    /// What serverless run and serverless test use.
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

        public static async Task<LocalAdapterHost> StartAsync(string packageDirectory, IDictionary<string, string> settings,
            AdapterRuntimeOptions runtimes = null, int commandTimeoutSeconds = 60, string workDirectory = null)
        {
            var manifest = AdapterManifest.Parse(File.ReadAllText(Path.Combine(packageDirectory, AdapterManifest.FileName)));
            var work = workDirectory ?? Path.Combine(Path.GetTempPath(), "swsl-local", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            var host = Build(runtimes ?? new AdapterRuntimeOptions(), commandTimeoutSeconds, work);
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
                return new LocalAdapterHost(host, session, work, workDirectory == null);
            }
            catch
            {
                await host.StopAsync();
                host.Dispose();
                if (workDirectory == null) try { Directory.Delete(work, true); } catch { }
                throw;
            }
        }

        /// <summary>Calls a command and returns its answer as text: raw for a string, JSON for anything else.</summary>
        public Task<string> CallAsync(string command, object input) => session.CallAsync(command, input);

        /// <summary>Calls a command that answers nothing.</summary>
        public Task CallVoidAsync(string command, object input) => session.CallVoidAsync(command, input);

        public async ValueTask DisposeAsync()
        {
            try { await session.DisposeAsync(); } catch { /* stopping only */ }
            await host.StopAsync();
            host.Dispose();
            if (ownsWork) try { Directory.Delete(work, true); } catch { }
        }

        static IHost Build(AdapterRuntimeOptions runtimes, int commandTimeoutSeconds, string work)
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
