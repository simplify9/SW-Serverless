using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SW.Serverless.Samples.CarrierContract;
using SW.Serverless.Sdk.Hosting;
using SW.Serverless.Sdk.Resident;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Carrier
{
    /// <summary>
    /// A typical carrier adapter — the shape the ~107 Traxis agent adapters already have — except
    /// that it STAYS RUNNING.
    ///
    /// The point is what does NOT change. The host calls it exactly as it calls a classic adapter:
    ///
    ///     await instance.InvokeAsync&lt;ShipmentResult&gt;("CreateShipment", request);
    ///
    /// Same command names, same JSON payloads, same result types. What changes is underneath:
    /// no process spawn, no JIT, no storage metadata check per call, and a gRPC channel that stays
    /// open and pooled across every invocation instead of being dialled and thrown away each time.
    ///
    /// Everything is constructor-injected: options bound from startup values, a gRPC client from
    /// the factory, a session-scoped call log, and an ILogger that lands in the host's own logs.
    /// </summary>
    public class CarrierHandler : IResidentAdapter, IResettable
    {
        readonly CarrierContract.Carrier.CarrierClient carrier;
        readonly CarrierOptions options;
        readonly CallLog callLog;
        readonly ILogger<CarrierHandler> logger;

        IAdapterContext context;
        long created, tracked, cancelled, upstreamFailures, retries;
        DateTimeOffset? lastCallOn;
        string lastError;
        volatile string state = "Starting";

        // Stop is terminal: once it has run, no in-flight call may report the adapter healthy again.
        volatile bool stopped;

        public CarrierHandler(CarrierContract.Carrier.CarrierClient carrier,
            IOptions<CarrierOptions> options, CallLog callLog, ILogger<CarrierHandler> logger)
        {
            this.carrier = carrier;
            this.options = options.Value;
            this.callLog = callLog;
            this.logger = logger;
        }

        // ------------------------------------------------------------------ lifecycle

        public async Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
        {
            this.context = context;

            // Fail fast and loudly: an adapter that cannot reach its upstream should say so at
            // start rather than on the first customer's shipment.
            try
            {
                var pong = await carrier.PingAsync(new PingRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(options.TimeoutSeconds),
                    cancellationToken: cancellationToken);

                state = "Connected";
                logger.LogInformation("Connected to {BaseUrl} — carrier {Version}.",
                    options.BaseUrl, pong.Version);
            }
            catch (Exception ex)
            {
                state = "Disconnected";
                lastError = ex.Message;
                logger.LogWarning(ex, "Could not reach the carrier at {BaseUrl} on start.", options.BaseUrl);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            stopped = true;
            state = "Stopped";
            logger.LogInformation("Carrier adapter stopping after {Created} shipments.", created);
            return Task.CompletedTask;
        }

        public Task<AdapterStatus> GetStatusAsync()
        {
            var status = new AdapterStatus
            {
                Connected = state == "Connected",
                State = state == "Connected" && created + tracked == 0 ? "Idle" : state,
                LastMessageOn = lastCallOn,
                LastError = lastError
            };

            status.Details["endpoint"] = options.BaseUrl;
            status.Details["account"] = options.Account ?? "(unset)";
            status.Details["shipmentsCreated"] = created.ToString();
            status.Details["shipmentsTracked"] = tracked.ToString();
            status.Details["cancelled"] = cancelled.ToString();
            status.Details["upstreamFailures"] = upstreamFailures.ToString();
            status.Details["retries"] = retries.ToString();
            status.Details["openSessions"] = callLog.SessionCount.ToString();

            return Task.FromResult(status);
        }

        /// <summary>
        /// The session boundary the pooled shape depends on. The host calls this when a lease is
        /// returned, and the per-session audit trail goes with it.
        /// </summary>
        public async Task ResetAsync(string sessionId)
        {
            if (options.ResetDelayMs > 0) await Task.Delay(options.ResetDelayMs);
            callLog.Clear(sessionId);
        }

        // ------------------------------------------------------------------ commands
        // Exactly the shape a classic adapter exposes: a public Task<T> per operation, discovered
        // by name, taking and returning the host SDK's own types.

        public async Task<ShipmentResult> CreateShipment(ShipmentRequest request)
        {
            if (request?.Pieces is not { Count: > 0 })
                return new ShipmentResult
                {
                    Succeeded = false,
                    ErrorCode = "NO_PIECES",
                    ErrorMessage = "The shipment has no pieces."
                };

            var upstream = new CreateShipmentRequest
            {
                Account = options.Account ?? "",
                Reference = request.Reference ?? "",
                Service = request.Service ?? options.DefaultService,
                Sender = Map(request.Sender),
                Recipient = Map(request.Recipient)
            };
            upstream.Parcels.AddRange(request.Pieces.Select(p => new Parcel
            {
                WeightKg = p.Weight,
                LengthCm = p.Length,
                WidthCm = p.Width,
                HeightCm = p.Height
            }));

            // Retrying a create is only safe because the carrier deduplicates on our reference and
            // replays the original shipment. With no reference there is nothing to deduplicate on,
            // so a retry after a timeout could book the same parcel twice — do not retry then.
            var reply = await CallAsync(nameof(CreateShipment),
                token => carrier.CreateShipmentAsync(upstream,
                    deadline: DateTime.UtcNow.AddSeconds(options.TimeoutSeconds),
                    cancellationToken: token),
                idempotent: !string.IsNullOrWhiteSpace(upstream.Reference));

            if (reply == null)
                return new ShipmentResult
                {
                    Succeeded = false,
                    ErrorCode = "UPSTREAM_UNAVAILABLE",
                    ErrorMessage = lastError
                };

            if (!reply.Accepted)
            {
                // A carrier rejection is a RESULT, not an exception. Throwing here would turn a
                // business outcome into an adapter outage — the pattern Traxis's
                // CreateShipmentAdapterBase exists to enforce, and which a third of its adapters
                // skip by deriving from raw AdapterBase.
                logger.LogInformation("Carrier rejected {Reference}: {Code} {Message}",
                    request.Reference, reply.ErrorCode, reply.ErrorMessage);

                return new ShipmentResult
                {
                    Succeeded = false,
                    ErrorCode = reply.ErrorCode,
                    ErrorMessage = reply.ErrorMessage
                };
            }

            created++;
            return new ShipmentResult
            {
                Succeeded = true,
                TrackingNumber = reply.TrackingNumber,
                LabelUrl = reply.LabelUrl,
                Price = (decimal)reply.Price,
                Currency = reply.Currency
            };
        }

        public async Task<TrackResult> Track(TrackRequest request)
        {
            var reply = await CallAsync(nameof(Track),
                token => carrier.TrackAsync(new CarrierContract.TrackRequest
                {
                    Account = options.Account ?? "",
                    TrackingNumber = request?.TrackingNumber ?? ""
                }, deadline: DateTime.UtcNow.AddSeconds(options.TimeoutSeconds), cancellationToken: token));

            if (reply is not { Found: true })
                return new TrackResult { Found = false, TrackingNumber = request?.TrackingNumber };

            tracked++;
            return new TrackResult
            {
                Found = true,
                TrackingNumber = reply.TrackingNumber,
                Status = reply.CurrentStatus,
                Traces = reply.Traces.Select(t => new TraceLine
                {
                    Status = t.Status,
                    Description = t.Description,
                    Location = t.Location,
                    OccurredOn = DateTimeOffset.FromUnixTimeMilliseconds(t.OccurredUnixMs)
                }).ToList()
            };
        }

        public async Task<object> CancelShipment(TrackRequest request)
        {
            var reply = await CallAsync(nameof(CancelShipment),
                token => carrier.CancelShipmentAsync(new CancelRequest
                {
                    Account = options.Account ?? "",
                    TrackingNumber = request?.TrackingNumber ?? ""
                }, deadline: DateTime.UtcNow.AddSeconds(options.TimeoutSeconds), cancellationToken: token));

            if (reply?.Cancelled == true) cancelled++;
            return new { cancelled = reply?.Cancelled ?? false, error = reply?.ErrorMessage ?? lastError };
        }

        /// <summary>
        /// The Traxis pattern, made safe. Gateway calls GetLogs after every command and merges the
        /// result into the shipment's audit record; here the entries belong to this session only.
        /// </summary>
        public Task<CallLogResult> GetLogs() => Task.FromResult(callLog.Current());

        public Task<object> TestConnection() => CallAsync(nameof(TestConnection),
            token => carrier.PingAsync(new PingRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: token))
            .ContinueWith(t => (object)new
            {
                ok = t.Result != null,
                endpoint = options.BaseUrl,
                version = t.Result?.Version,
                error = t.Result == null ? lastError : null
            });

        // ------------------------------------------------------------------ plumbing

        static Address Map(Party party) => party == null ? new Address() : new Address
        {
            Name = party.Name ?? "",
            Line1 = party.Street ?? "",
            City = party.City ?? "",
            Postcode = party.PostCode ?? "",
            Country = party.Country ?? ""
        };

        /// <summary>
        /// One place for deadlines, retries, timing and the audit entry, so no command has to
        /// remember them — which is exactly what a base class does for the Traxis adapters.
        /// </summary>
        async Task<TReply> CallAsync<TReply>(string operation,
            Func<CancellationToken, AsyncUnaryCall<TReply>> call, bool idempotent = true)
            where TReply : class
        {
            var clock = Stopwatch.StartNew();

            for (var attempt = 1; attempt <= Math.Max(1, options.MaxAttempts); attempt++)
            {
                try
                {
                    var reply = await call(context?.Stopping ?? CancellationToken.None);

                    clock.Stop();
                    lastCallOn = DateTimeOffset.UtcNow;

                    // A failed attempt may have parked the adapter in "Disconnected"; a success
                    // clears that. Shutdown wins, so a call landing after StopAsync cannot
                    // resurrect the adapter's reported state.
                    if (!stopped) state = "Connected";

                    callLog.Record(operation, clock.Elapsed, true,
                        attempt > 1 ? $"succeeded on attempt {attempt}" : null);

                    context?.Metric("carrier.calls", 1);
                    logger.LogDebug("{Operation} took {Ms} ms.", operation, clock.ElapsedMilliseconds);

                    return reply;
                }
                catch (RpcException ex) when (idempotent && Transient(ex) && attempt < options.MaxAttempts)
                {
                    retries++;
                    logger.LogWarning("{Operation} failed with {Status}; retrying ({Attempt}/{Max}).",
                        operation, ex.StatusCode, attempt, options.MaxAttempts);

                    // Without the stop token the loop sits out the whole delay during shutdown and
                    // then makes one more attempt with an already-cancelled token.
                    await Task.Delay(options.RetryDelayMs * attempt, context?.Stopping ?? CancellationToken.None);
                }
                catch (Exception ex)
                {
                    clock.Stop();
                    upstreamFailures++;
                    lastError = ex.Message;
                    state = ex is RpcException { StatusCode: StatusCode.Unavailable }
                        ? "Disconnected" : state;

                    callLog.Record(operation, clock.Elapsed, false, ex.Message);
                    logger.LogError(ex, "{Operation} failed.", operation);
                    return null;
                }
            }

            upstreamFailures++;
            lastError = $"{operation} exhausted {options.MaxAttempts} attempts.";
            callLog.Record(operation, clock.Elapsed, false, lastError);
            return null;
        }

        static bool Transient(RpcException ex) => ex.StatusCode
            is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted;
    }
}
