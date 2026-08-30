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
        public ServerlessService(ServerlessOptions serverlessOptions, IMemoryCache memoryCache, ILoggerFactory loggerFactory, IServiceProvider serviceProvider, ICloudFilesService cloudFilesService)
        {
            this.serverlessOptions = serverlessOptions;
            this.memoryCache = memoryCache;
            this.loggerFactory = loggerFactory;
            this.cloudFilesService = cloudFilesService;

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

            if (!File.Exists(adapterMetadata.LocalPath))
                throw new FileNotFoundException(
                    $"Adapter '{adapterId}' package does not contain the entry assembly '{Path.GetFileName(adapterMetadata.LocalPath)}' named in its metadata.",
                    adapterMetadata.LocalPath);

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

            if (startupValues == null) startupValues = new Dictionary<string, string>();
            startupValues.Add(Constants.CorrelationIdName, correlationId);

            adapterLogger = loggerFactory.CreateLogger($"{adaptersNamingPrefix}.{adapterId}".ToLower());


            var startupValuesBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(startupValues)));
            var serverlessOptionsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(serverlessOptions)));
            var adapterValuesBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(adapterMetadata.AdapterValues)));

            process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    Arguments = $"\"{adapterMetadata.LocalPath}\" {serverlessOptionsBase64} {startupValuesBase64} {adapterValuesBase64}",
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

            return Task.CompletedTask;
        }

        public Task<IDictionary<string, StartupValue>> GetExpectedStartupValues()
        {
            return InvokeAsync<IDictionary<string, StartupValue>>(Constants.ExpectedCommand, null);
        }

        async public Task InvokeAsync(string command, object input, int commandTimeout = 0)
        {
            await InvokeAsync<NoT>(command, input, commandTimeout);
        }

        async public Task<TResult> InvokeAsync<TResult>(string command, object input, int commandTimeout = 0)
        {
            if (commandTimeout == 0) commandTimeout = serverlessOptions.CommandTimeout;

            if (string.IsNullOrWhiteSpace(command) || command.Contains(' '))
            {
                throw new ArgumentException("Invalid name.", nameof(command));
            }

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
                else
                {
                    //anything the adapter writes to stderr without going through AdapterLogger,
                    //such as a host startup failure. Dropping it hides why the process died.
                    adapterLogger.LogWarning(args.Data.Replace(Constants.NewLineIdentifier, "\n"));
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
            var adapterMetadata = await GetAdapterMetadata(adapterId);
            var adapterDiretoryPath = $"{serverlessOptions.AdapterLocalPath}/{adapterMetadata.Hash}";
            //var adapterPath = Path.GetFullPath($"{adapterDiretoryPath}/{adapterConfig.EntryAssembly}");

            await semaphoreSlim.WaitAsync();
            try
            {
                if (!Directory.Exists(adapterDiretoryPath))
                {
                    Directory.CreateDirectory(adapterDiretoryPath);
                    try
                    {
                        //using var cloudFilesService = new CloudFilesService(serverlessOptions.CloudFilesOptions);
                        using var stream = await cloudFilesService.OpenReadAsync($"{serverlessOptions.AdapterRemotePath}/{adapterId}".ToLower());
                        using var archive = new ZipArchive(stream);

                        foreach (var entry in archive.Entries)
                        {
                            var path = $"{adapterDiretoryPath}/{entry.FullName.Replace("\\", "/")}";
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            entry.ExtractToFile(path);
                        }


                        //Process.Start("chmod", $"755 {adapterPath}").WaitForExit(5000);
                    }
                    catch (Exception)
                    {
                        Directory.Delete(adapterDiretoryPath, true);
                        throw;
                    }
                }
            }
            finally
            {
                semaphoreSlim.Release();
            }

            return adapterMetadata;
        }

        async Task<AdapterMetadata> GetAdapterMetadata(string adapterId)
        {
            if (memoryCache.TryGetValue($"{adaptersNamingPrefix}.{adapterId}", out AdapterMetadata adapterMetadata))
                return adapterMetadata;

            //using var cloudFilesService = new CloudFilesService(serverlessOptions.CloudFilesOptions);

            var metadataPath = $"{serverlessOptions.AdapterRemotePath}/{adapterId}".ToLower();
            var cloudMetadata = await cloudFilesService.GetMetadataAsync(metadataPath);
            var metaData = new Dictionary<string, string>(cloudMetadata, StringComparer.OrdinalIgnoreCase);

            if (!metaData.TryGetValue("EntryAssembly", out var entryAssembly) ||
                string.IsNullOrWhiteSpace(entryAssembly))
                throw new InvalidOperationException(
                    $"Adapter '{adapterId}' metadata at '{metadataPath}' is missing 'EntryAssembly'.");

            if (!metaData.TryGetValue("Hash", out var hash) || string.IsNullOrWhiteSpace(hash))
                throw new InvalidOperationException(
                    $"Adapter '{adapterId}' metadata at '{metadataPath}' is missing 'Hash'.");

            adapterMetadata = new AdapterMetadata
            {
                EntryAssembly = entryAssembly,
                Hash = hash,
                AdapterValues = metaData
            };

            adapterMetadata.LocalPath = Path.GetFullPath($"{serverlessOptions.AdapterLocalPath}/{adapterMetadata.Hash}/{adapterMetadata.EntryAssembly}");


            return memoryCache.Set($"{adaptersNamingPrefix}.{adapterId}", adapterMetadata, TimeSpan.FromMinutes(serverlessOptions.AdapterMetadataCacheDuration));
        }


        public void Dispose()
        {
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

        }

        private class NoT
        {
        }

        private class AdapterMetadata
        {
            public string Hash { get; set; }
            public string EntryAssembly { get; set; }
            public string LocalPath { get; set; }
            public IDictionary<string, string> AdapterValues { get; set; } = new Dictionary<string, string>();
        }
    }
}
