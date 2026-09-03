using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Newtonsoft.Json;
using SW.Serverless.Contract;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SW.Serverless.Sdk.Resident
{
    /// <summary>
    /// The adapter side of protocol 2. Dials the host over a Unix domain socket (Linux / macOS)
    /// or a named pipe (Windows) and pumps one bidirectional gRPC stream.
    /// See the design doc, section 15.
    /// </summary>
    public sealed class ResidentRunner : IAdapterContext
    {
        const int ProtocolVersion = 2;

        readonly object handler;
        readonly IResidentAdapter resident;
        readonly IResettable resettable;
        readonly Dictionary<string, HandlerMethodInfo> commands;

        readonly Channel<AdapterFrame> outbound =
            Channel.CreateBounded<AdapterFrame>(new BoundedChannelOptions(2048)
            {
                // Logging must never apply backpressure to the data path — design doc 12.2.
                FullMode = BoundedChannelFullMode.DropWrite
            });

        readonly ConcurrentDictionary<long, TaskCompletionSource<EventAck>> pendingEvents = new();
        readonly CancellationTokenSource stopping = new();

        Handshake handshake;
        SemaphoreSlim inFlight;
        long nextId;
        long droppedFrames;

        IReadOnlyDictionary<string, string> startupValues = new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> adapterValues = new Dictionary<string, string>();

        ResidentRunner(object handler)
        {
            this.handler = handler;
            resident = handler as IResidentAdapter;
            resettable = handler as IResettable;
            commands = BuildCommands(handler);
        }

        // ------------------------------------------------------------------ entry point

        public static async Task RunAsync(object handler)
        {
            var runner = new ResidentRunner(handler);
            try
            {
                await runner.RunCoreAsync();
            }
            catch (Exception ex)
            {
                AdapterLogger.LogError(ex, "Resident adapter terminated.");
                Environment.ExitCode = 1;
            }
        }

        async Task RunCoreAsync()
        {
            var line = await Console.In.ReadLineAsync();

            // A null read means the parent is gone. v1 spun here forever — design doc 3, item 7.
            if (line == null)
                throw new IOException("stdin closed before the handshake arrived; parent process is gone.");

            handshake = Handshake.Parse(line);
            if (handshake.Protocol != ProtocolVersion)
                throw new NotSupportedException(
                    $"Host speaks protocol {handshake.Protocol}, this SDK speaks {ProtocolVersion}.");

            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = Transport.CreateHandler(handshake),
                // Payloads are opaque and can be large; the host also caps this on its side.
                MaxReceiveMessageSize = 64 * 1024 * 1024,
                MaxSendMessageSize = 64 * 1024 * 1024
            });

            var client = new AdapterHost.AdapterHostClient(channel);
            using var call = client.Attach(cancellationToken: stopping.Token);

            // Single writer task. gRPC request streams are not safe for concurrent writes,
            // and batching through one channel is what keeps the syscall count down.
            var writer = Task.Run(() => PumpOutboundAsync(call.RequestStream));
            var stdinWatch = Task.Run(WatchParentAsync);

            await call.RequestStream.WriteAsync(new AdapterFrame
            {
                Hello = new Hello
                {
                    Token = handshake.Token ?? "",
                    AdapterId = handshake.AdapterId ?? "",
                    InstanceKey = handshake.InstanceKey ?? "",
                    ProtocolVersion = ProtocolVersion,
                    SdkVersion = typeof(ResidentRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    Capabilities = { Capabilities() }
                }
            });

            await ReadLoopAsync(call.ResponseStream);

            stopping.Cancel();
            outbound.Writer.TryComplete();
            await Task.WhenAny(writer, Task.Delay(2000));
            try { await call.RequestStream.CompleteAsync(); } catch { /* already torn down */ }
            _ = stdinWatch;
        }

        IEnumerable<string> Capabilities()
        {
            if (resident != null) yield return "resident";
            if (resettable != null) yield return "resettable";
            foreach (var c in commands.Keys) yield return "command:" + c;
        }

        // ------------------------------------------------------------------ read loop

        async Task ReadLoopAsync(IAsyncStreamReader<HostFrame> stream)
        {
            while (await MoveNextSafe(stream))
            {
                var frame = stream.Current;
                switch (frame.BodyCase)
                {
                    case HostFrame.BodyOneofCase.Ready:
                        await OnReadyAsync(frame.Ready);
                        break;

                    case HostFrame.BodyOneofCase.Invoke:
                        // Deliberately not awaited: a slow command must not block the read loop,
                        // which is the whole point of multiplexing (design doc 3, item 1).
                        _ = Task.Run(() => OnInvokeAsync(frame.Id, frame.Invoke));
                        break;

                    case HostFrame.BodyOneofCase.Ping:
                        _ = Task.Run(() => OnPingAsync(frame.Id));
                        break;

                    case HostFrame.BodyOneofCase.EventAck:
                        if (pendingEvents.TryRemove(frame.Id, out var tcs))
                            tcs.TrySetResult(frame.EventAck);
                        break;

                    case HostFrame.BodyOneofCase.SetLogLevel:
                        MinimumLogLevel = (AdapterLogLevel)frame.SetLogLevel.Level;
                        break;

                    case HostFrame.BodyOneofCase.Reset:
                        if (resettable != null) await resettable.ResetAsync(frame.Reset.SessionId);
                        break;

                    case HostFrame.BodyOneofCase.Shutdown:
                        await OnShutdownAsync(frame.Shutdown);
                        return;
                }
            }
        }

        static async Task<bool> MoveNextSafe(IAsyncStreamReader<HostFrame> stream)
        {
            try { return await stream.MoveNext(CancellationToken.None); }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { return false; }
        }

        async Task OnReadyAsync(Ready ready)
        {
            startupValues = new Dictionary<string, string>(ready.StartupValues, StringComparer.OrdinalIgnoreCase);
            adapterValues = new Dictionary<string, string>(ready.AdapterValues, StringComparer.OrdinalIgnoreCase);
            inFlight = new SemaphoreSlim(Math.Max(1, ready.MaxInFlight));

            if (resident != null)
                await resident.StartAsync(this, stopping.Token);
        }

        async Task OnInvokeAsync(long id, Invoke invoke)
        {
            try
            {
                if (!commands.TryGetValue(invoke.Command, out var method))
                    throw new MissingMethodException(handler.GetType().FullName, invoke.Command);

                object arg = null;
                if (method.ParameterType != null && !invoke.Payload.IsEmpty)
                {
                    var text = invoke.Payload.ToStringUtf8();
                    arg = method.ParameterType == typeof(string)
                        ? text
                        : method.ParameterType == typeof(byte[])
                            ? invoke.Payload.ToByteArray()
                            : JsonConvert.DeserializeObject(text, method.ParameterType);
                }

                var task = (Task)(method.ParameterType == null
                    ? method.MethodInfo.Invoke(handler, null)
                    : method.MethodInfo.Invoke(handler, new[] { arg }));

                await task.ConfigureAwait(false);

                ByteString payload = ByteString.Empty;
                if (!method.Void)
                {
                    var result = task.GetType().GetProperty(nameof(Task<object>.Result))?.GetValue(task);
                    if (result != null)
                        payload = result is byte[] bytes
                            ? ByteString.CopyFrom(bytes)
                            : ByteString.CopyFromUtf8(result is string s ? s : JsonConvert.SerializeObject(result));
                }

                Send(new AdapterFrame { Id = id, InvokeResult = new InvokeResult { Payload = payload } });
            }
            catch (Exception ex)
            {
                var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                Send(new AdapterFrame
                {
                    Id = id,
                    InvokeResult = new InvokeResult
                    {
                        Error = new Error
                        {
                            Type = real.GetType().FullName ?? "Exception",
                            Message = real.Message ?? "",
                            Detail = real.ToString()
                        }
                    }
                });
            }
        }

        async Task OnPingAsync(long id)
        {
            var pong = new Pong { State = "Running", Connected = true };
            try
            {
                if (resident != null)
                {
                    var status = await resident.GetStatusAsync() ?? new AdapterStatus();
                    pong.Connected = status.Connected;
                    pong.State = status.State ?? "Unknown";
                    pong.InFlight = status.InFlight;
                    pong.LastError = status.LastError ?? "";
                    if (status.LastMessageOn.HasValue)
                        pong.LastMessageUnixMs = status.LastMessageOn.Value.ToUnixTimeMilliseconds();
                    foreach (var kv in status.Details) pong.Details[kv.Key] = kv.Value ?? "";
                }
            }
            catch (Exception ex)
            {
                pong.Connected = false;
                pong.State = "StatusFailed";
                pong.LastError = ex.Message;
            }

            if (droppedFrames > 0) pong.Details["sdk.droppedFrames"] = droppedFrames.ToString();
            Send(new AdapterFrame { Id = id, Pong = pong });
        }

        async Task OnShutdownAsync(Shutdown shutdown)
        {
            stopping.Cancel();
            if (resident != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(shutdown.Drain ? 30 : 5));
                try { await resident.StopAsync(cts.Token); }
                catch (Exception ex) { AdapterLogger.LogWarning(ex, "StopAsync threw."); }
            }
        }

        /// <summary>If the host dies our stdin reaches EOF. Exit rather than orphan-spin.</summary>
        async Task WatchParentAsync()
        {
            try { while (await Console.In.ReadLineAsync() != null) { } }
            catch { /* closed */ }
            stopping.Cancel();
        }

        // ------------------------------------------------------------------ outbound

        void Send(AdapterFrame frame)
        {
            if (!outbound.Writer.TryWrite(frame))
                Interlocked.Increment(ref droppedFrames);
        }

        async Task PumpOutboundAsync(IClientStreamWriter<AdapterFrame> stream)
        {
            try
            {
                await foreach (var frame in outbound.Reader.ReadAllAsync())
                    await stream.WriteAsync(frame);
            }
            catch (Exception ex)
            {
                AdapterLogger.LogWarning(ex, "Outbound pump stopped.");
                stopping.Cancel();
            }
        }

        // ------------------------------------------------------------------ IAdapterContext

        public string AdapterId => handshake?.AdapterId;
        public string InstanceKey => handshake?.InstanceKey;
        public IReadOnlyDictionary<string, string> StartupValues => startupValues;
        public IReadOnlyDictionary<string, string> AdapterValues => adapterValues;
        public CancellationToken Stopping => stopping.Token;
        public AdapterLogLevel MinimumLogLevel { get; private set; } = AdapterLogLevel.Information;

        public string StartupValueOf(string name) =>
            startupValues.TryGetValue(name, out var v) ? v : null;

        public async Task<PublishResult> PublishAsync(
            ReadOnlyMemory<byte> payload, string dedupeKey, string endpoint = null,
            IDictionary<string, string> headers = null, string contentType = null,
            CancellationToken cancellationToken = default)
        {
            if (inFlight != null) await inFlight.WaitAsync(cancellationToken);
            try
            {
                var id = Interlocked.Increment(ref nextId);
                var tcs = new TaskCompletionSource<EventAck>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingEvents[id] = tcs;

                var ev = new Event
                {
                    Payload = ByteString.CopyFrom(payload.Span),
                    DedupeKey = dedupeKey ?? "",
                    ContentType = contentType ?? "application/octet-stream",
                    Endpoint = endpoint ?? ""
                };
                if (headers != null)
                    foreach (var kv in headers) ev.Headers[kv.Key] = kv.Value ?? "";

                // Bypass the droppable channel: an event must never be silently dropped.
                await outbound.Writer.WriteAsync(new AdapterFrame { Id = id, Event = ev }, cancellationToken);

                using (cancellationToken.Register(() => tcs.TrySetCanceled()))
                using (stopping.Token.Register(() => tcs.TrySetCanceled()))
                {
                    var ack = await tcs.Task;
                    return new PublishResult
                    {
                        Accepted = ack.Accepted,
                        Reference = ack.Reference,
                        Error = ack.Error?.Message
                    };
                }
            }
            finally
            {
                inFlight?.Release();
            }
        }

        public void Log(AdapterLogLevel level, string message, Exception exception = null,
            IDictionary<string, string> properties = null)
        {
            if (level < MinimumLogLevel) return;

            var entry = new LogEntry
            {
                Level = (int)level,
                Message = message ?? "",
                Exception = exception?.ToString() ?? "",
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (properties != null)
                foreach (var kv in properties) entry.Properties[kv.Key] = kv.Value ?? "";

            Send(new AdapterFrame { Log = entry });
        }

        public void LogInformation(string message, IDictionary<string, string> properties = null) =>
            Log(AdapterLogLevel.Information, message, null, properties);

        public void LogWarning(string message, Exception exception = null) =>
            Log(AdapterLogLevel.Warning, message, exception);

        public void LogError(string message, Exception exception = null) =>
            Log(AdapterLogLevel.Error, message, exception);

        public void Metric(string name, double value, IDictionary<string, string> tags = null)
        {
            var m = new Metric { Name = name, Value = value };
            if (tags != null)
                foreach (var kv in tags) m.Tags[kv.Key] = kv.Value ?? "";
            Send(new AdapterFrame { Metric = m });
        }

        // ------------------------------------------------------------------ command discovery

        static Dictionary<string, HandlerMethodInfo> BuildCommands(object handler)
        {
            var map = new Dictionary<string, HandlerMethodInfo>(StringComparer.OrdinalIgnoreCase);
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "StartAsync", "StopAsync", "GetStatusAsync", "ResetAsync" };

            foreach (var m in handler.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (m.IsGenericMethod || m.GetParameters().Length > 1) continue;
                if (m.DeclaringType == typeof(object) || skip.Contains(m.Name)) continue;

                var returnsTask = m.ReturnType == typeof(Task);
                var returnsTaskOf = m.ReturnType.IsGenericType &&
                                    m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>);
                if (!returnsTask && !returnsTaskOf) continue;

                map[m.Name] = new HandlerMethodInfo
                {
                    MethodInfo = m,
                    Void = returnsTask,
                    ParameterType = m.GetParameters().Length == 1 ? m.GetParameters()[0].ParameterType : null
                };
            }
            return map;
        }
    }

    static class Transport
    {
        /// <summary>
        /// The address handed to GrpcChannel is a placeholder — ConnectCallback intercepts before
        /// any DNS or TCP happens, so nothing here touches the network stack.
        /// </summary>
        public static System.Net.Http.SocketsHttpHandler CreateHandler(Handshake handshake) =>
            new System.Net.Http.SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = false,
                ConnectCallback = async (_, ct) =>
                {
                    if (!string.IsNullOrWhiteSpace(handshake.Pipe))
                    {
                        var pipe = new NamedPipeClientStream(".", handshake.Pipe,
                            PipeDirection.InOut, PipeOptions.WriteThrough | PipeOptions.Asynchronous);
                        await pipe.ConnectAsync(ct);
                        return pipe;
                    }

                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(handshake.Socket), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };
    }
}
