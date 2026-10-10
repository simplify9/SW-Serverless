using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SW.PrimitiveTypes;
using SW.Serverless.Sdk;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless
{
    public class ServerlessService : IServerlessService, IDisposable
    {
        private static readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);
        private const string adaptersNamingPrefix = "serverless.adapters";
        private readonly ServerlessOptions serverlessOptions;
        private readonly IMemoryCache memoryCache;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<ServerlessService> logger;
        private Process process;
        private object taskCompletionSource;
        private MethodInfo trySetResultMethod;
        private MethodInfo trySetTrySetExceptionMethod;
        private Timer invocationTimeoutTimer;
        private bool processStarted;
        private volatile bool timedOut;
        private ILogger adapterLogger;
        private readonly ICloudFilesService cloudFilesService;
        private readonly AdapterInstaller installer;
        private readonly IServiceProvider serviceProvider;
        private IDisposable directoryLease;

        // Set when the adapter speaks the gRPC protocol rather than the classic text one: every
        // non-.NET adapter, and a .NET one whose manifest opts in. The session then runs on a
        // resident instance of its own, started here and stopped on dispose.
        private GrpcSession grpc;
        /// <summary>
        /// Additive overload for hosts that never install adapters from cloud storage — the
        /// StartAsync(adapterId, correlationId, adapterPath, ...) path needs no ICloudFilesService.
        /// DI selects this automatically when none is registered.
        /// </summary>
        public ServerlessService(ServerlessOptions serverlessOptions, IMemoryCache memoryCache,
            ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
            : this(serverlessOptions, memoryCache, loggerFactory, serviceProvider,
                   serviceProvider.GetService<ICloudFilesService>())
        {
        }

        public ServerlessService(ServerlessOptions serverlessOptions, IMemoryCache memoryCache, ILoggerFactory loggerFactory, IServiceProvider serviceProvider, ICloudFilesService cloudFilesService)
        {
            this.serverlessOptions = serverlessOptions;
            this.memoryCache = memoryCache;
            this.loggerFactory = loggerFactory;
            this.cloudFilesService = cloudFilesService;
            this.serviceProvider = serviceProvider;
            installer = new AdapterInstaller(serverlessOptions, memoryCache, cloudFilesService,
                serviceProvider?.GetService<Runtimes.AdapterRuntimes>());

            logger = loggerFactory.CreateLogger<ServerlessService>();

            // if (serverlessOptions.CloudFilesOptions == null)
            // {
            //     serverlessOptions.CloudFilesOptions = serviceProvider.GetService<CloudFilesOptions>();
            // }

            //cloudFilesOptions = serverlessOptions.CloudFilesOptions;
        }

        async public Task StartAsync(string adapterId, string correlationId, IDictionary<string, string> startupValues = null)
        {
            if (string.IsNullOrWhiteSpace(adapterId) || adapterId.Contains(' '))
            {
                throw new ArgumentException("Invalid name.", nameof(adapterId));
            }

            var adapterMetadata = await Install(adapterId);

            if (adapterMetadata.UsesGrpc)
            {
                if (processStarted || grpc != null)
                    throw new Exception("Already started.");
                grpc = await GrpcSession.StartAsync(serviceProvider, adapterId, correlationId, startupValues,
                    adapterMetadata.AdapterValues);
                return;
            }

            // Held while this process runs, so publishing a newer version can't prune its files.
            directoryLease = AdapterDirectoryLeases.Hold(adapterMetadata.Directory);

            await StartAsync(adapterId, adapterMetadata, correlationId, startupValues);
        }

        async public Task StartAsync(string adapterId, string correlationId, string adapterPath, IDictionary<string, string> startupValues = null)
        {
            if (!File.Exists(adapterPath))
                throw new FileNotFoundException(adapterPath);

            var fakeMetadata = new AdapterMetadata
            {
                LocalPath = adapterPath
            };

            await StartAsync(adapterId, fakeMetadata, correlationId, startupValues);

        }

        Task StartAsync(string adapterId, AdapterMetadata adapterMetadata, string correlationId, IDictionary<string, string> startupValues = null)
        {
            if (processStarted)
                throw new Exception("Already started.");

            // Copy rather than mutate. The caller's dictionary is often a long-lived entity's own
            // settings, passed straight in — so adding CorrelationId to it
            // leaked into that entity, and a second call with the same dictionary threw
            // "An item with the same key has already been added".
            var values = startupValues == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(startupValues);

            values[Constants.CorrelationIdName] = correlationId;
            startupValues = values;

            adapterLogger = loggerFactory.CreateLogger($"{adaptersNamingPrefix}.{adapterId}".ToLower());


            var startupValuesBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(startupValues)));
            var serverlessOptionsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(serverlessOptions)));
            var adapterValuesBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(adapterMetadata.AdapterValues)));

            // On stdin for an adapter whose SDK reads them there, as arguments for every other.
            var valuesOnStdin = adapterMetadata.ValuesOnStdin;
            process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    Arguments = valuesOnStdin
                        ? $"\"{adapterMetadata.LocalPath}\" {Constants.ValuesOnStdinFlag}"
                        : $"\"{adapterMetadata.LocalPath}\" {serverlessOptionsBase64} {startupValuesBase64} {adapterValuesBase64}",
                    WorkingDirectory = Path.GetDirectoryName(adapterMetadata.LocalPath),
                    UseShellExecute = false,

                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,

                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };
            
            process.OutputDataReceived += OutputDataReceived;
            process.ErrorDataReceived += ErrorDataReceived;

            if (!process.Start())
                throw new Exception("Process reused!");

            processStarted = true;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (valuesOnStdin)
            {
                process.StandardInput.WriteLine(serverlessOptionsBase64);
                process.StandardInput.WriteLine(startupValuesBase64);
                process.StandardInput.WriteLine(adapterValuesBase64);
                process.StandardInput.Flush();
            }

            return Task.CompletedTask;
        }

        public Task<IDictionary<string, StartupValue>> GetExpectedStartupValues()
        {
            if (grpc != null) return Task.FromResult(grpc.ExpectedStartupValues());
            return InvokeAsync<IDictionary<string, StartupValue>>(Constants.ExpectedCommand, null);
        }

        async public Task InvokeAsync(string command, object input, int commandTimeout = 0)
        {
            if (grpc != null)
            {
                await grpc.InvokeAsync(command, input, commandTimeout == 0 ? serverlessOptions.CommandTimeout : commandTimeout);
                return;
            }
            await InvokeAsync<NoT>(command, input, commandTimeout);
        }

        async public Task<TResult> InvokeAsync<TResult>(string command, object input, int commandTimeout = 0)
        {
            if (commandTimeout == 0) commandTimeout = serverlessOptions.CommandTimeout;

            if (string.IsNullOrWhiteSpace(command) || command.Contains(' '))
            {
                throw new ArgumentException("Invalid name.", nameof(command));
            }

            if (grpc != null)
                return await grpc.InvokeAsync<TResult>(command, input, commandTimeout);

            if (!processStarted || process.HasExited || timedOut)
                throw new Exception("Process not started or terminated.");

            taskCompletionSource = new TaskCompletionSource<TResult>();
            trySetResultMethod = taskCompletionSource.GetType().GetMethod("TrySetResult");
            trySetTrySetExceptionMethod = taskCompletionSource.GetType().GetMethod("TrySetException", new Type[] { typeof(Exception) });

            invocationTimeoutTimer = new Timer(
                callback: InvocationTimeoutTimerCallback,
                state: null,
                dueTime: TimeSpan.FromSeconds(commandTimeout),
                period: Timeout.InfiniteTimeSpan);

            string inputString;

            if (input == null)
                inputString = Constants.NullIdentifier;
            else if (input.GetType() == typeof(string) || input.GetType().IsPrimitive)
                inputString = input.ToString();
            else
                inputString = JsonConvert.SerializeObject(input);


            await process.StandardInput.WriteLineAsync($"{Constants.Delimiter}{command}{Constants.Delimiter}{inputString}{Constants.Delimiter}".Replace("\n", Constants.NewLineIdentifier));

            return await ((TaskCompletionSource<TResult>)taskCompletionSource).Task;
        }

        void InvocationTimeoutTimerCallback(object state)
        {
            invocationTimeoutTimer.Dispose();

            // The process is reused for multiple sequential commands on the same invocation
            // (e.g. CreateShipment followed by GetLogs in a finally block). If we leave a
            // timed-out process running, its eventual late output is delivered to whichever
            // taskCompletionSource is current by then - a later, unrelated invocation - and
            // silently resolves it with the wrong (stale) result instead of its own.
            //
            // Order matters here: kill (and mark this instance permanently dead) BEFORE
            // completing the caller's Task. Completing the Task first would let the awaiter's
            // continuation - e.g. that same finally-block GetLogs follow-up - start a new
            // InvokeAsync and overwrite taskCompletionSource while the old process is still
            // alive, racing the kill below. Killing first, and sticking `timedOut` regardless
            // of whether Kill() itself throws, guarantees no further output can ever arrive and
            // that the next InvokeAsync on this instance fails fast via the "process not
            // started or terminated" guard instead of hanging or being mis-resolved.
            timedOut = true;
            try
            {
                if (processStarted && !process.HasExited)
                    process.Kill();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill timed-out adapter process.");
            }
            finally
            {
                trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new TimeoutException() });
            }
        }

        void ErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            try
            {
                if (args.Data == null)
                {
                    //adapterLogger.LogWarning("Null data received on error stream.");
                }
                else if (args.Data.StartsWith(Constants.LogInformationIdentifier))
                {
                    adapterLogger.LogInformation(args.Data.Replace(Constants.LogInformationIdentifier, "").Replace(Constants.NewLineIdentifier, "\n"));
                }
                else if (args.Data.StartsWith(Constants.LogWarningIdentifier))
                {
                    adapterLogger.LogWarning(args.Data.Replace(Constants.LogWarningIdentifier, "").Replace(Constants.NewLineIdentifier, "\n"));
                }
                else if (args.Data.StartsWith(Constants.LogErrorIdentifier))
                {
                    adapterLogger.LogError(args.Data.Replace(Constants.LogErrorIdentifier, "").Replace(Constants.NewLineIdentifier, "\n"));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in ErrorDataReceived.");
            }
        }

        void OutputDataReceived(object sender, DataReceivedEventArgs args)
        {
            try
            {
                invocationTimeoutTimer?.Dispose();

                if (args.Data == null)
                {
                    if (taskCompletionSource != null)
                        trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new Exception("Received null data.") });
                    return;
                }

                if (args.Data.StartsWith(Constants.ErrorIdentifier) && taskCompletionSource != null)
                {
                    trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new Exception(args.Data) });
                    return;
                }

                var outputSegments = args.Data.Split(Constants.Delimiter);

                if (outputSegments.Length != 3 && taskCompletionSource != null)
                {
                    trySetTrySetExceptionMethod.Invoke(taskCompletionSource, new object[] { new Exception("Wrong data format.") });
                    return;
                }

                var outputDenormalized = outputSegments[1].Replace(Constants.NewLineIdentifier, "\n");

                var returnType = taskCompletionSource.GetType().GetGenericArguments()[0];
                object resultTyped;

                if (outputDenormalized == Constants.NullIdentifier)
                    resultTyped = null;
                else if (returnType == typeof(string))
                    resultTyped = outputDenormalized;
                else if (returnType.IsPrimitive)
                    resultTyped = Convert.ChangeType(outputDenormalized, returnType);
                else if (returnType == typeof(NoT))
                    resultTyped = new NoT();
                else
                    resultTyped = JsonConvert.DeserializeObject(outputDenormalized, returnType);

                trySetResultMethod.Invoke(taskCompletionSource, new object[] { resultTyped });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in OutputDataReceived. The adapter may be using an incompatible version of the Serverless SDK.");
                if (taskCompletionSource != null)
                    trySetTrySetExceptionMethod?.Invoke(taskCompletionSource, new object[] { ex });
            }
        }

        async Task<AdapterMetadata> Install(string adapterId)
        {
            var installed = await installer.InstallAsync(adapterId);
            return new AdapterMetadata
            {
                Hash = installed.Hash,
                EntryAssembly = installed.EntryAssembly,
                LocalPath = installed.LocalPath,
                Directory = installed.Directory,
                AdapterValues = installed.AdapterValues,
                UsesGrpc = UsesGrpc(installed),
                ValuesOnStdin = ReadsValuesOnStdin(installed)
            };
        }

        /// <summary>
        /// Whether a classic call to this adapter goes over gRPC: always for a runtime other than
        /// .NET, whose SDKs speak only that, and for a .NET adapter whose manifest opts in with a
        /// protocol of 2 or more. Every other .NET adapter keeps the text protocol it was built for.
        /// </summary>
        /// <summary>
        /// Whether the adapter's SDK reads its values from stdin: known only from the SDK version
        /// its manifest records. A package without one is from before, and gets them as arguments.
        /// </summary>
        internal static bool ReadsValuesOnStdin(InstalledAdapter installed) =>
            Version.TryParse((installed.Manifest?.SdkVersion ?? "").Split('-', '+')[0], out var sdk) &&
            sdk >= Version.Parse(Constants.ValuesOnStdinSince);

        internal static bool UsesGrpc(InstalledAdapter installed) =>
            !string.Equals(installed.Runtime, Contract.Catalog.AdapterManifest.DotnetRuntime, StringComparison.OrdinalIgnoreCase) ||
            installed.Manifest?.Protocol is { Min: >= 2 };

        public void Dispose()
        {
            if (grpc != null)
            {
                try
                {
                    grpc.StopAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Service did not dispose properly.");
                }
                grpc = null;
                return;
            }

            try
            {
                if (processStarted)
                {

                    if (!process.HasExited)
                    {
                        process.StandardInput.WriteLine(Constants.QuitCommand);
                        process.WaitForExit(3000);
                        if (!process.HasExited) process.Kill();
                    }

                    process.Dispose();

                    invocationTimeoutTimer?.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Service did not dispose properly.");
            }
            finally
            {
                directoryLease?.Dispose();
            }

        }

        private class NoT
        {
        }

        private class AdapterMetadata
        {
            public string Hash { get; set; }
            public string EntryAssembly { get; set; }
            public string LocalPath { get; set; }
            public string Directory { get; set; }
            public IDictionary<string, string> AdapterValues { get; set; } = new Dictionary<string, string>();
            public bool UsesGrpc { get; set; }
            public bool ValuesOnStdin { get; set; }
        }

        /// <summary>
        /// A classic session over gRPC: a resident instance under a key of its own, started for
        /// the session and stopped when it ends, called one command at a time as the text protocol
        /// is. The resident host does the launching, handshake, timeouts and logs.
        /// </summary>
        private sealed class GrpcSession
        {
            readonly Resident.IResidentAdapterHost host;
            readonly string adapterId;
            readonly string instanceKey;
            readonly Resident.ResidentAdapterInstance instance;

            GrpcSession(Resident.IResidentAdapterHost host, string adapterId, string instanceKey,
                Resident.ResidentAdapterInstance instance)
            {
                this.host = host;
                this.adapterId = adapterId;
                this.instanceKey = instanceKey;
                this.instance = instance;
            }

            public static async Task<GrpcSession> StartAsync(IServiceProvider services, string adapterId,
                string correlationId, IDictionary<string, string> startupValues, IDictionary<string, string> adapterValues)
            {
                var host = services?.GetService<Resident.IResidentAdapterHost>()
                           ?? throw new InvalidOperationException(
                               $"Adapter '{adapterId}' speaks the gRPC protocol, which runs on the resident adapter host. " +
                               "Register it with AddResidentAdapters.");

                var values = startupValues == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(startupValues);
                values[Constants.CorrelationIdName] = correlationId;

                var instanceKey = $"classic-{Guid.NewGuid():N}";
                var instance = await host.StartExclusiveAsync(new Resident.AdapterSpec
                {
                    AdapterId = adapterId,
                    InstanceKey = instanceKey,
                    StartupValues = values,
                    AdapterValues = adapterValues ?? new Dictionary<string, string>(),
                });
                return new GrpcSession(host, adapterId, instanceKey, instance);
            }

            public Task<TResult> InvokeAsync<TResult>(string command, object input, int timeoutSeconds) =>
                instance.InvokeAsync<TResult>(command, input, timeoutSeconds);

            public Task InvokeAsync(string command, object input, int timeoutSeconds) =>
                instance.InvokeAsync<object>(command, input, timeoutSeconds);

            /// <summary>The settings it declared in its handshake, as the classic protocol has always described them.</summary>
            public IDictionary<string, StartupValue> ExpectedStartupValues() =>
                instance.Settings.ToDictionary(
                    s => s.Name,
                    s => new StartupValue
                    {
                        Optional = !s.Required,
                        Default = string.IsNullOrEmpty(s.DefaultValue) ? null : s.DefaultValue,
                        Type = s.Type,
                        Private = s.Secret,
                        Description = s.Description,
                    });

            public Task StopAsync() => host.StopAsync(adapterId, instanceKey, drain: false);
        }
    }
}
