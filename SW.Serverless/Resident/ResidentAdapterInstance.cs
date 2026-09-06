using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SW.Serverless.Contract;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    public enum InstanceState { Spawning, Attached, Ready, Draining, Stopped, Quarantined }

    /// <summary>
    /// One live adapter process, as the host sees it. Callers hold a HANDLE, never ownership —
    /// a DI scope ending must not kill a broker connection (design doc 4).
    /// </summary>
    public sealed class ResidentAdapterInstance : IAsyncDisposable
    {
        readonly ResidentOptions options;
        readonly ILogger logger;
        readonly ILogger adapterLogger;
        readonly IAdapterEventSink sink;

        // The correlation fix: every outstanding call is keyed, so a late reply can never
        // resolve an unrelated one the way the single v1 field did (design doc 14.3).
        readonly ConcurrentDictionary<long, PendingCall> pending = new();

        readonly Channel<HostFrame> outbound = Channel.CreateUnbounded<HostFrame>(
            new UnboundedChannelOptions { SingleReader = true });

        readonly ConcurrentQueue<string> diagnostics = new();
        readonly SemaphoreSlim inbound;
        readonly TaskCompletionSource<bool> attached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        long nextId;
        bool shutdownRequested;
        Task writerTask;
        CancellationTokenSource linkedCts;

        internal ResidentAdapterInstance(string adapterId, string instanceKey, string token,
            ResidentOptions options, IAdapterEventSink sink, ILoggerFactory loggerFactory)
        {
            AdapterId = adapterId;
            InstanceKey = instanceKey;
            Token = token;
            this.options = options;
            this.sink = sink;
            inbound = new SemaphoreSlim(Math.Max(1, options.MaxInFlight));
            logger = loggerFactory.CreateLogger<ResidentAdapterInstance>();
            adapterLogger = loggerFactory.CreateLogger($"serverless.adapters.{adapterId}".ToLowerInvariant());
        }

        public string AdapterId { get; }
        public string InstanceKey { get; }
        internal string Token { get; }
        public InstanceState State { get; private set; } = InstanceState.Spawning;
        public Process Process { get; internal set; }
        public DateTimeOffset StartedOn { get; } = DateTimeOffset.UtcNow;
        public int RestartCount { get; internal set; }
        public Pong LastStatus { get; private set; }
        public IReadOnlyDictionary<string, string> StartupValues { get; internal set; }

        /// <summary>
        /// What the adapter said it can do, from its Hello frame: "resident", "resettable", and
        /// "command:{Name}" for every command it discovered on its handler. A UI can build itself
        /// from this instead of hardcoding what each adapter offers.
        /// </summary>
        public IReadOnlyCollection<string> Capabilities { get; internal set; } = Array.Empty<string>();

        public IReadOnlyCollection<string> Commands { get; internal set; } = Array.Empty<string>();

        public string SdkVersion { get; internal set; }
        public int ProtocolVersion { get; internal set; }
        public IReadOnlyDictionary<string, string> AdapterValues { get; internal set; }

        public IReadOnlyCollection<string> Diagnostics => diagnostics.ToArray();

        /// <summary>Completes when the adapter has attached and been made Ready.</summary>
        public Task Attached => attached.Task;

        internal void AddDiagnostic(string line)
        {
            if (line == null) return;
            diagnostics.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {line}");
            while (diagnostics.Count > options.DiagnosticBufferLines) diagnostics.TryDequeue(out _);
        }

        // ------------------------------------------------------------------ stream pump

        internal async Task RunStreamAsync(IAsyncStreamReader<AdapterFrame> input,
            IServerStreamWriter<HostFrame> output, CancellationToken cancellationToken)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in outbound.Reader.ReadAllAsync(linkedCts.Token))
                        await output.WriteAsync(frame);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // A writer that dies silently leaves every subsequent call to hang until its
                    // own timeout. Treat it as what it is: the stream is gone.
                    logger.LogWarning(ex, "Outbound writer for {AdapterId}/{InstanceKey} failed.",
                        AdapterId, InstanceKey);
                    linkedCts.Cancel();
                }
            }, linkedCts.Token);

            State = InstanceState.Attached;

            Send(new HostFrame
            {
                Ready = new Ready
                {
                    MaxInFlight = options.MaxInFlight,
                    StartupValues = { (IDictionary<string, string>)(StartupValues == null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(StartupValues)) },
                    AdapterValues = { (IDictionary<string, string>)(AdapterValues == null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(AdapterValues)) }
                }
            });

            State = InstanceState.Ready;
            attached.TrySetResult(true);

            try
            {
                while (await input.MoveNext(cancellationToken))
                    await OnFrameAsync(input.Current, cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // A torn-down stream is the expected end of a requested shutdown, not a fault.
                if (shutdownRequested || cancellationToken.IsCancellationRequested)
                    logger.LogDebug("Adapter {AdapterId}/{InstanceKey} stream closed during shutdown.",
                        AdapterId, InstanceKey);
                else
                    logger.LogWarning(ex, "Adapter {AdapterId}/{InstanceKey} stream faulted.", AdapterId, InstanceKey);
            }
            finally
            {
                State = InstanceState.Stopped;
                FailAllPending(new IOException("Adapter stream closed."));
            }
        }

        async Task OnFrameAsync(AdapterFrame frame, CancellationToken ct)
        {
            switch (frame.BodyCase)
            {
                case AdapterFrame.BodyOneofCase.InvokeResult:
                    // Match on kind too. Ids are unique across calls today, but resolving a
                    // pending Invoke from whatever frame happens to carry its id is the same class
                    // of defect as the v1 single-completion-source, and worth closing by shape
                    // rather than by trusting the adapter to behave.
                    if (pending.TryGetValue(frame.Id, out var expecting)
                        && expecting.Kind is CallKind.Ping)
                        break;

                    if (pending.TryRemove(frame.Id, out var call))
                    {
                        call.Timer?.Dispose();
                        if (frame.InvokeResult.Error != null &&
                            !string.IsNullOrEmpty(frame.InvokeResult.Error.Type))
                            call.Completion.TrySetException(new AdapterInvocationException(
                                frame.InvokeResult.Error.Type,
                                frame.InvokeResult.Error.Message,
                                frame.InvokeResult.Error.Detail));
                        else
                            call.Completion.TrySetResult(frame.InvokeResult.Payload.ToByteArray());
                    }
                    break;

                case AdapterFrame.BodyOneofCase.Pong:
                    LastStatus = frame.Pong;
                    if (pending.TryGetValue(frame.Id, out var expectingPong)
                        && expectingPong.Kind is not CallKind.Ping)
                        break;

                    if (pending.TryRemove(frame.Id, out var ping))
                    {
                        ping.Timer?.Dispose();
                        ping.Completion.TrySetResult(Array.Empty<byte>());
                    }
                    break;

                case AdapterFrame.BodyOneofCase.Event:
                    // Not awaited, so several events run concurrently — but bounded by the same
                    // window the adapter was granted. Without this the host advertised a credit
                    // limit and then accepted unbounded concurrency from an adapter that ignored
                    // it, which is the failure mode the window exists to prevent.
                    await inbound.WaitAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        try { await HandleEventAsync(frame, ct); }
                        finally { inbound.Release(); }
                    }, ct);
                    break;

                case AdapterFrame.BodyOneofCase.Log:
                    WriteLog(frame.Log);
                    break;

                case AdapterFrame.BodyOneofCase.Metric:
                    AdapterMetrics.Record(AdapterId, InstanceKey, frame.Metric);
                    break;
            }
        }

        async Task HandleEventAsync(AdapterFrame frame, CancellationToken ct)
        {
            EventOutcome outcome;
            try
            {
                outcome = await sink.OnEventAsync(new InboundEvent
                {
                    AdapterId = AdapterId,
                    InstanceKey = InstanceKey,
                    Endpoint = frame.Event.Endpoint,
                    DedupeKey = frame.Event.DedupeKey,
                    ContentType = frame.Event.ContentType,
                    Payload = frame.Event.Payload.ToByteArray(),
                    Headers = new Dictionary<string, string>(frame.Event.Headers),
                    Traceparent = frame.Traceparent
                }, ct) ?? EventOutcome.Rejected("Sink returned null.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Event sink threw for {AdapterId}/{InstanceKey}.", AdapterId, InstanceKey);
                outcome = EventOutcome.Rejected(ex.Message);
            }

            var ack = new EventAck { Accepted = outcome.Accepted, Reference = outcome.Reference ?? "" };
            if (!outcome.Accepted)
                ack.Error = new Error { Type = "SinkRejected", Message = outcome.Error ?? "" };

            Send(new HostFrame { Id = frame.Id, EventAck = ack });
        }

        void WriteLog(LogEntry entry)
        {
            var level = (LogLevel)Math.Clamp(entry.Level, 0, 5);
            using var scope = adapterLogger.BeginScope(new Dictionary<string, object>
            {
                ["adapterId"] = AdapterId,
                ["instanceKey"] = InstanceKey
            });
            if (string.IsNullOrEmpty(entry.Exception))
                adapterLogger.Log(level, "{Message}", entry.Message);
            else
                adapterLogger.Log(level, "{Message} {Exception}", entry.Message, entry.Exception);
        }

        // ------------------------------------------------------------------ host -> adapter

        void Send(HostFrame frame) => outbound.Writer.TryWrite(frame);

        public async Task<TResult> InvokeAsync<TResult>(string command, object input = null,
            int timeoutSeconds = 0, CancellationToken cancellationToken = default,
            string sessionId = null)
        {
            var bytes = await InvokeAsync(command, Serialize(input), timeoutSeconds,
                cancellationToken, sessionId);
            if (bytes == null || bytes.Length == 0) return default;
            if (typeof(TResult) == typeof(byte[])) return (TResult)(object)bytes;
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (typeof(TResult) == typeof(string)) return (TResult)(object)text;
            return JsonConvert.DeserializeObject<TResult>(text);
        }

        public async Task<byte[]> InvokeAsync(string command, byte[] payload = null,
            int timeoutSeconds = 0, CancellationToken cancellationToken = default,
            string sessionId = null)
        {
            if (State != InstanceState.Ready)
                throw new InvalidOperationException($"Adapter {AdapterId} is {State}, not Ready.");

            var id = Interlocked.Increment(ref nextId);
            var call = new PendingCall
            {
                Kind = CallKind.Invoke,
                Completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            pending[id] = call;

            var timeout = timeoutSeconds > 0
                ? TimeSpan.FromSeconds(timeoutSeconds)
                : options.InvokeTimeout;

            // On timeout the call is REMOVED, so a late reply is discarded rather than
            // resolving the next caller's completion — the v1 defect (design doc 14.3).
            call.Timer = new Timer(_ =>
            {
                if (pending.TryRemove(id, out var c))
                {
                    c.Timer?.Dispose();
                    c.Completion.TrySetException(new TimeoutException(
                        $"Adapter '{AdapterId}' did not answer '{command}' within {timeout}."));
                }
            }, null, timeout, Timeout.InfiniteTimeSpan);

            Send(new HostFrame
            {
                Id = id,
                Traceparent = Activity.Current?.Id ?? "",
                Invoke = new Invoke
                {
                    Command = command,
                    Payload = payload == null ? ByteString.Empty : ByteString.CopyFrom(payload),
                    TimeoutSeconds = (int)timeout.TotalSeconds,
                    SessionId = sessionId ?? ""
                }
            });

            using (cancellationToken.Register(() =>
            {
                if (pending.TryRemove(id, out var c))
                {
                    c.Timer?.Dispose();
                    c.Completion.TrySetCanceled();
                }
            }))
            {
                try { return await call.Completion.Task; }
                finally { call.Timer?.Dispose(); }
            }
        }

        public async Task<Pong> PingAsync(TimeSpan timeout)
        {
            var id = Interlocked.Increment(ref nextId);
            var call = new PendingCall
            {
                Kind = CallKind.Ping,
                Completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            pending[id] = call;
            call.Timer = new Timer(_ =>
            {
                if (pending.TryRemove(id, out var c))
                {
                    c.Timer?.Dispose();
                    c.Completion.TrySetException(new TimeoutException("Heartbeat timed out."));
                }
            }, null, timeout, Timeout.InfiniteTimeSpan);

            Send(new HostFrame { Id = id, Ping = new Ping() });
            await call.Completion.Task;
            return LastStatus;
        }

        /// <summary>
        /// Waits for the adapter to confirm the reset. Fire-and-forget was wrong: the pool treated
        /// a completed task as proof the session boundary had taken effect and returned the
        /// instance to idle, so the next lease could be handed a process that had not yet cleared
        /// the previous session's state — the exact leak the boundary exists to prevent.
        /// </summary>
        public async Task ResetAsync(string sessionId, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (State != InstanceState.Ready)
                throw new InvalidOperationException($"Adapter {AdapterId} is {State}, not Ready.");

            var id = Interlocked.Increment(ref nextId);
            var call = new PendingCall
            {
                Kind = CallKind.Reset,
                Completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            pending[id] = call;

            var deadline = timeout ?? TimeSpan.FromSeconds(15);
            call.Timer = new Timer(_ =>
            {
                if (pending.TryRemove(id, out var c))
                {
                    c.Timer?.Dispose();
                    c.Completion.TrySetException(new TimeoutException(
                        $"Adapter '{AdapterId}' did not confirm the session reset within {deadline}."));
                }
            }, null, deadline, Timeout.InfiniteTimeSpan);

            Send(new HostFrame { Id = id, Reset = new Reset { SessionId = sessionId ?? "" } });

            using (cancellationToken.Register(() =>
            {
                if (pending.TryRemove(id, out var c))
                {
                    c.Timer?.Dispose();
                    c.Completion.TrySetCanceled();
                }
            }))
            {
                try { await call.Completion.Task; }
                finally { call.Timer?.Dispose(); }
            }
        }

        public Task SetLogLevelAsync(LogLevel level)
        {
            Send(new HostFrame { SetLogLevel = new SetLogLevel { Level = (int)level } });
            return Task.CompletedTask;
        }

        internal void RequestShutdown(string reason, bool drain)
        {
            shutdownRequested = true;
            State = InstanceState.Draining;
            Send(new HostFrame { Shutdown = new Shutdown { Reason = reason ?? "", Drain = drain } });
        }

        internal void MarkQuarantined() => State = InstanceState.Quarantined;

        static byte[] Serialize(object input) => input switch
        {
            null => null,
            byte[] b => b,
            string s => System.Text.Encoding.UTF8.GetBytes(s),
            _ => System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(input))
        };

        void FailAllPending(Exception ex)
        {
            foreach (var key in pending.Keys)
                if (pending.TryRemove(key, out var c))
                {
                    c.Timer?.Dispose();
                    c.Completion.TrySetException(ex);
                }
            attached.TrySetException(ex);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                linkedCts?.Cancel();
                outbound.Writer.TryComplete();
                if (writerTask != null) await Task.WhenAny(writerTask, Task.Delay(1000));
            }
            catch { /* teardown */ }
            linkedCts?.Dispose();
        }

        enum CallKind { Invoke, Ping, Reset }

        sealed class PendingCall
        {
            public TaskCompletionSource<byte[]> Completion;
            public Timer Timer;
            public CallKind Kind;
        }
    }

    public class AdapterInvocationException : Exception
    {
        public AdapterInvocationException(string type, string message, string detail)
            : base($"{type}: {message}")
        {
            AdapterExceptionType = type;
            Detail = detail;
        }

        public string AdapterExceptionType { get; }
        public string Detail { get; }
    }
}
