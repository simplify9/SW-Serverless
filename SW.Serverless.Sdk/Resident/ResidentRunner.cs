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
using AdapterSession = SW.Serverless.Sdk.Hosting.AdapterSession;
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

        readonly Type handlerType;
        readonly Func<IAdapterContext, object> handlerFactory;
        readonly Dictionary<string, HandlerMethodInfo> commands;

        object handler;
        IResidentAdapter resident;
        IResettable resettable;

        // Two channels, because they have opposite requirements. Telemetry is droppable and must
        // never apply backpressure to the data path; events and command results must never be
        // dropped. Sharing one channel meant a log storm could still block an event write even
        // though the logs themselves were droppable.
        readonly Channel<AdapterFrame> priority =
            Channel.CreateUnbounded<AdapterFrame>(new UnboundedChannelOptions { SingleReader = true });

        readonly Channel<AdapterFrame> telemetry =
            Channel.CreateBounded<AdapterFrame>(new BoundedChannelOptions(2048)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            });

        readonly ConcurrentDictionary<long, TaskCompletionSource<EventAck>> pendingEvents = new();
        readonly ConcurrentDictionary<long, TaskCompletionSource<StateResult>> pendingState = new();
        readonly CancellationTokenSource stopping = new();

        Handshake handshake;
        SemaphoreSlim inFlight;
        long nextId;
        long droppedFrames;

        IReadOnlyDictionary<string, string> startupValues = new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> adapterValues = new Dictionary<string, string>();

        static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

        // Per async flow, so concurrent commands on one shared instance do not read each other's.
        static readonly AsyncLocal<IReadOnlyDictionary<string, string>> invocationValues = new();

        ResidentRunner(Type handlerType, Func<IAdapterContext, object> handlerFactory)
        {
            this.handlerType = handlerType;
            this.handlerFactory = handlerFactory;

            // Discovered from the TYPE, so Hello can advertise the commands before startup values
            // have arrived and before the handler itself exists.
            commands = BuildCommands(handlerType);
        }

        // ------------------------------------------------------------------ entry point

        public static Task RunAsync(object handler) =>
            RunAsync(handler.GetType(), _ => handler);

        /// <summary>
        /// The handler is built only once startup values are in hand, which is what lets a
        /// DI container inject configuration into its constructor.
        /// </summary>
        public static async Task RunAsync(Type handlerType, Func<IAdapterContext, object> handlerFactory)
        {
            var runner = new ResidentRunner(handlerType, handlerFactory);
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

            var client = new Contract.AdapterHost.AdapterHostClient(channel);
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
                    Capabilities = { Capabilities() },
                    Commands = { CommandInfos() }
                }
            });

            await ReadLoopAsync(call.ResponseStream);

            stopping.Cancel();
            priority.Writer.TryComplete();
            telemetry.Writer.TryComplete();
            await Task.WhenAny(writer, Task.Delay(2000));
            try { await call.RequestStream.CompleteAsync(); } catch { /* already torn down */ }
            _ = stdinWatch;
        }

        /// <summary>
        /// The same commands `capabilities` names, with the shape a caller needs to actually
        /// invoke one: whether it takes an argument, what that argument looks like, and whether
        /// anything comes back.
        /// </summary>
        IEnumerable<CommandInfo> CommandInfos()
        {
            foreach (var pair in commands)
            {
                var method = pair.Value.MethodInfo;
                var parameterType = pair.Value.ParameterType;

                var info = new CommandInfo
                {
                    Name = pair.Key,
                    ParameterType = parameterType?.Name ?? "",
                    ParameterSchema = DescribeParameter(parameterType),
                    ReturnsValue = !pair.Value.Void,
                    Description = method.GetCustomAttribute<AdapterCommandAttribute>()?.Description ?? ""
                };

                yield return info;
            }
        }

        /// <summary>
        /// A complex argument's public properties as name -> type, so a UI can build a form for a
        /// command it has never seen. Primitives and strings describe themselves through
        /// ParameterType, so they get nothing here rather than a pointless one-entry object.
        /// </summary>
        static string DescribeParameter(Type parameterType)
        {
            if (parameterType == null) return "";
            if (parameterType.IsPrimitive || parameterType == typeof(string) ||
                parameterType == typeof(decimal) || parameterType == typeof(DateTime) ||
                parameterType == typeof(Guid))
                return "";

            try
            {
                var properties = parameterType
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead)
                    .ToDictionary(p => p.Name, p => FriendlyName(p.PropertyType));

                return properties.Count == 0
                    ? ""
                    : System.Text.Json.JsonSerializer.Serialize(properties);
            }
            catch
            {
                // Describing an argument must never stop an adapter attaching.
                return "";
            }
        }

        static string FriendlyName(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return FriendlyName(underlying) + "?";
            if (type.IsArray) return FriendlyName(type.GetElementType()) + "[]";
            if (type.IsGenericType)
                return type.Name.Split('`')[0] + "<" +
                       string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
            return type.Name;
        }

        IEnumerable<string> Capabilities()
        {
            if (typeof(IResidentAdapter).IsAssignableFrom(handlerType)) yield return "resident";
            if (typeof(IResettable).IsAssignableFrom(handlerType)) yield return "resettable";
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

                    case HostFrame.BodyOneofCase.StateResult:
                        if (pendingState.TryRemove(frame.Id, out var stateTcs))
                            stateTcs.TrySetResult(frame.StateResult);
                        break;

                    case HostFrame.BodyOneofCase.SetLogLevel:
                        MinimumLogLevel = (AdapterLogLevel)frame.SetLogLevel.Level;
                        break;

                    case HostFrame.BodyOneofCase.Reset:
                        // Answered, not fire-and-forget. The pool waits for this before handing the
                        // process to another session; without the reply it could hand over a
                        // process that has not yet cleared the previous session's state.
                        _ = Task.Run(() => OnResetAsync(frame.Id, frame.Reset.SessionId));
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

            // Now, and only now, is it safe to construct the handler.
            handler = handlerFactory(this);
            resident = handler as IResidentAdapter;
            resettable = handler as IResettable;

            if (resident != null)
                await resident.StartAsync(this, stopping.Token);
        }

        async Task OnInvokeAsync(long id, Invoke invoke)
        {
            try
            {
                if (handler == null)
                    throw new InvalidOperationException("The adapter has not been made ready yet.");

                if (!commands.TryGetValue(invoke.Command, out var method))
                    throw new MissingMethodException(handlerType.FullName, invoke.Command);

                // Set on THIS async flow, before the handler runs, so concurrent invocations on a
                // shared instance each see their own caller's configuration. AsyncLocal rather than
                // a field for exactly that reason: several commands are in flight at once by
                // design, and a field would have the last one in overwrite the rest.
                invocationValues.Value = invoke.Properties.Count == 0
                    ? Empty
                    : new Dictionary<string, string>(invoke.Properties);

                // The host's session id when it grouped this call with others, otherwise the
                // call stands alone.
                using var session = AdapterSession.Begin(
                    string.IsNullOrEmpty(invoke.SessionId) ? id.ToString() : invoke.SessionId,
                    invoke.Command);

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

        async Task OnResetAsync(long id, string sessionId)
        {
            var result = new InvokeResult();
            try
            {
                if (resettable != null) await resettable.ResetAsync(sessionId);
            }
            catch (Exception ex)
            {
                result.Error = new Error
                {
                    Type = ex.GetType().FullName ?? "Exception",
                    Message = ex.Message ?? "",
                    Detail = ex.ToString()
                };
            }

            Send(new AdapterFrame { Id = id, InvokeResult = result });
        }

        async Task OnShutdownAsync(Shutdown shutdown)
        {
            // Cancelling `stopping` FIRST made drain impossible: the adapter's own token — the one
            // its consume loop and PublishAsync calls observe — was already cancelled, so there
            // was nothing left to finish. Ask it to stop, let it drain, and only then cancel.
            if (resident != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(shutdown.Drain ? 30 : 5));
                try { await resident.StopAsync(cts.Token); }
                catch (Exception ex) { AdapterLogger.LogWarning(ex, "StopAsync threw."); }
            }

            stopping.Cancel();
        }

        /// <summary>If the host dies our stdin reaches EOF. Exit rather than orphan-spin.</summary>
        async Task WatchParentAsync()
        {
            try { while (await Console.In.ReadLineAsync() != null) { } }
            catch { /* closed */ }
            stopping.Cancel();
        }

        // ------------------------------------------------------------------ outbound

        /// <summary>Command results, pongs and events. Never dropped.</summary>
        void Send(AdapterFrame frame) => priority.Writer.TryWrite(frame);

        /// <summary>Logs and metrics. Dropped rather than allowed to block anything.</summary>
        void SendTelemetry(AdapterFrame frame)
        {
            if (!telemetry.Writer.TryWrite(frame))
                Interlocked.Increment(ref droppedFrames);
        }

        /// <summary>
        /// One writer for both channels, because a gRPC request stream is not safe for concurrent
        /// writes. Priority frames are drained first so a telemetry backlog cannot delay a
        /// heartbeat response and get a healthy adapter restarted.
        /// </summary>
        async Task PumpOutboundAsync(IClientStreamWriter<AdapterFrame> stream)
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    while (priority.Reader.TryRead(out var urgent))
                        await stream.WriteAsync(urgent);

                    if (telemetry.Reader.TryRead(out var frame))
                    {
                        await stream.WriteAsync(frame);
                        continue;
                    }

                    var ready = await Task.WhenAny(
                        priority.Reader.WaitToReadAsync(stopping.Token).AsTask(),
                        telemetry.Reader.WaitToReadAsync(stopping.Token).AsTask());

                    if (!await ready) break;
                }
            }
            catch (OperationCanceledException) { }
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

        public IReadOnlyDictionary<string, string> InvocationValues => invocationValues.Value ?? Empty;

        public string ValueOf(string name)
        {
            var perCall = invocationValues.Value;
            if (perCall != null && perCall.TryGetValue(name, out var value)) return value;

            return StartupValueOf(name);
        }

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

                try
                {
                    Send(new AdapterFrame { Id = id, Event = ev });

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
                    // Removed on every path, not only on ack. Leaving cancelled entries behind
                    // grows the dictionary for the life of a process meant to run for weeks.
                    pendingEvents.TryRemove(id, out _);
                }
            }
            finally
            {
                inFlight?.Release();
            }
        }

        public async Task<string> GetStateAsync(string name, CancellationToken cancellationToken = default)
        {
            var result = await StateAsync(
                new StateRequest { Op = StateRequest.Types.Op.Get, Name = Named(name) }, cancellationToken);

            return result.Found ? result.Value : null;
        }

        public async Task SetStateAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            var request = value == null
                ? new StateRequest { Op = StateRequest.Types.Op.Delete, Name = Named(name) }
                : new StateRequest { Op = StateRequest.Types.Op.Set, Name = Named(name), Value = value };

            await StateAsync(request, cancellationToken);
        }

        static string Named(string name) =>
            string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("A state name is required.", nameof(name))
                : name;

        /// <summary>
        /// One state round trip. Deliberately NOT bounded by the in-flight window that guards
        /// PublishAsync: that window exists to stop an adapter flooding the host with messages it
        /// must persist, and a receiver saving its cursor after a batch would then be queued behind
        /// the very events whose progress it is recording.
        /// </summary>
        async Task<StateResult> StateAsync(StateRequest request, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<StateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingState[id] = tcs;

            try
            {
                Send(new AdapterFrame { Id = id, State = request });

                using (cancellationToken.Register(() => tcs.TrySetCanceled()))
                using (stopping.Token.Register(() => tcs.TrySetCanceled()))
                {
                    var result = await tcs.Task;

                    // Surfaced rather than swallowed: a cursor that silently failed to save is a
                    // batch that will be replayed, and the adapter is the only thing in a position
                    // to stop rather than carry on.
                    if (result.Error != null && !string.IsNullOrEmpty(result.Error.Message))
                        throw new InvalidOperationException(
                            $"The host could not {request.Op.ToString().ToLowerInvariant()} state "
                            + $"'{request.Name}': {result.Error.Message}");

                    return result;
                }
            }
            finally
            {
                pendingState.TryRemove(id, out _);
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

            SendTelemetry(new AdapterFrame { Log = entry });
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
            SendTelemetry(new AdapterFrame { Metric = m });
        }

        // ------------------------------------------------------------------ command discovery

        static Dictionary<string, HandlerMethodInfo> BuildCommands(Type handlerType)
        {
            var map = new Dictionary<string, HandlerMethodInfo>(StringComparer.OrdinalIgnoreCase);
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "StartAsync", "StopAsync", "GetStatusAsync", "ResetAsync" };

            foreach (var m in handlerType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
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
                        try { await pipe.ConnectAsync(ct); }
                        catch { await pipe.DisposeAsync(); throw; }
                        return pipe;
                    }

                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(handshake.Socket), ct);
                    }
                    catch
                    {
                        // A retried connect would otherwise leak a handle on every attempt.
                        socket.Dispose();
                        throw;
                    }
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };
    }
}
