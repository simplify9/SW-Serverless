# Resident adapters — protocol 2

The design lives in [resident-adapters-design.md](resident-adapters-design.md). This page is the
short version plus how to run the samples.

## What changed, in one line per side

```csharp
// adapter — the ONLY difference. Same zip, same S3 metadata, same install, same spawn.
static Task Main() => Runner.Run(new Handler());          // classic, per invocation
static Task Main() => Runner.RunResident(new Handler());  // stays running
```

```csharp
// host
services.AddResidentAdapters<MyEventSink>(o => o.HeartbeatInterval = TimeSpan.FromSeconds(15));
```

Classic adapters are **untouched**. An adapter whose cloud metadata has no `Protocol` key takes
the v1 code path byte for byte — which is what keeps the existing fleet alive.

## Dependency injection in an adapter

`Runner.Run(new Handler())` still works and is unchanged. When an adapter grows past a single
class, `AdapterHost` gives it the same shape as any .NET service:

```csharp
static Task Main() => AdapterHost.CreateBuilder()
    .ConfigureServices((configuration, services) =>
    {
        services.Configure<StreamOptions>(configuration);   // bound from startup values
        services.AddSingleton<IChunkReader, ChunkReader>();
        services.AddHttpClient();
    })
    .Build<MyHandler>()
    .RunResidentAsync();                                    // or .RunAsync() for the classic path
```

You get, without asking for it:

* **`ILogger<T>`** routed onto the adapter's log channel, so anything your services — or the
  libraries they use — log reaches the host under `serverless.adapters.{id}`.
* **`IConfiguration`** built from startup values, with cloud metadata namespaced under
  `AdapterValues:` so it can never shadow them.
* **`IAdapterContext`** for pushing events and metrics, injectable anywhere.
* **`AdapterSession.Id`** — ambient per-invocation identity, and the boundary a pooled adapter
  needs so state cannot leak between checkouts.

**This also removes the constructor footgun.** The container is built *after* startup values
arrive, so injecting `IOptions<T>` into a constructor is safe — unlike
`Runner.Run(new Handler())`, where the handler exists before argv has been parsed.

Only `SWSL_`-prefixed environment variables are bound, and startup values outrank them. Binding
the environment unprefixed is a trap: a setting named `Path` picks up the machine's `PATH`.

## Run the samples

Two hosts, for two kinds of look. Build the solution first — both package adapters from their
build output.

For the RabbitMQ pair, start a broker first — without one those two adapters simply do not start
and the rest of the dashboard is unaffected, which is the intended failure mode:

```bash
docker run -d --rm -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management
```

**Web dashboard** — live observability, both lifecycles side by side:

```bash
dotnet run --project SW.Serverless.SampleWeb
```

**Console** — the same runtime with no UI, if you would rather read a log:

```bash
dotnet run --project SW.Serverless.Samples.Host
```

Both start by packaging the sample adapters into a local-filesystem cloud store
(`AddLocalTestsCloudFiles`, no credentials) and then starting them **by adapter id only** — so
the download, extract and launch steps are exercised, not skipped. That is the
"install a provider without redeploying" claim actually running.

| Sample | What it is for |
|---|---|
| `SW.Serverless.Samples.Ticker` | Smallest resident adapter. Proves attach, push/ack, heartbeat, runtime reconfiguration and typed command errors. No dependencies. |
| `SW.Serverless.Samples.FolderSource` | The reference **data source** shape — ingress, egress, topology, discovery, test-connection, real status — using a folder instead of a broker. A RabbitMQ or Kafka adapter is this class with a different client. |
| `SW.Serverless.Samples.RabbitMq` | Shared connection handling for the two broker samples. Everything is a startup value — host, vhost, exchange type, queue arguments — because a provider must not be opinionated about the broker's own model. Reconnection is deliberately **not** retried in a loop: the adapter reports itself disconnected and lets the supervisor decide, where backoff and crash-loop quarantine already live. |
| `SW.Serverless.Samples.RabbitPublisher` | **Egress.** Publishes every 10 ms (~100/s) with publisher confirms and `mandatory: true`, so unroutable messages come back through `BasicReturn` instead of vanishing. `SetInterval` changes the rate while running. |
| `SW.Serverless.Samples.RabbitConsumer` | **Ingress.** Declares a queue and binding, consumes with `autoAck: false`, and **only calls `BasicAck` after the host has acknowledged**. A host rejection becomes `BasicNack(requeue: true)`. This is the ordering to copy for a Kafka offset commit. `SetPrefetch` changes the broker-side backpressure dial at runtime. |
| `SW.Serverless.Samples.LargeFiles` | **Streaming, and what visibility looks like under load.** Streams a file in configurable chunks and pushes each to the host, so resident memory tracks the *chunk* size and not the file size. Reports percent, MB/s, ETA and its own working set on the heartbeat, which the dashboard renders as a live progress bar. `GenerateTestFile` makes a file of any size on demand; `Pause` / `Resume` hold a transfer mid-file. Built entirely on constructor injection — options, a reader service, a throughput meter and an `ILogger`. |
| `SW.Serverless.Samples.Carrier` | **A typical adapter, resident.** Does what a Traxis agent adapter does — takes the host's shipment type, translates it to a carrier's gRPC API, calls it with deadlines and retries, returns the host's result type. The host invokes it *exactly* as it invokes a classic adapter; what changes is underneath. Built on constructor injection: options, a pooled gRPC client, a session-scoped call log, an `ILogger`. Implements `IResettable`, so it is safely poolable. |
| `SW.Serverless.Samples.CarrierContract` | The carrier's own `.proto`. It belongs to the carrier, not to SW.Serverless — the adapter's job is translating between it and the host's SDK types. |
| `SW.Serverless.Samples.Classic` | A conventional **non-resident** adapter — `Runner.Run`, no `Protocol` metadata key, so the host takes the v1 path for it byte for byte. Shows startup values and the `{{expected}}` schema, typed commands, `AdapterLogger`, failures and timeouts, and process-static state. |
| `SW.Serverless.Samples.Host` | Console host. Implements `IAdapterEventSink`, starts both adapters, prints events and heartbeats. |
| `SW.Serverless.SampleWeb` | Blazor Server dashboard plus minimal APIs. Live adapter health, event feed, adapter logs and metrics, failure injection, and a page for the classic per-invocation lifecycle to contrast against. |

### What the dashboard shows

| Page | Why it is there |
|---|---|
| **Adapters** | Health from two independent sources — host-observed memory, CPU, threads, restarts and missed heartbeats, which keep working when an adapter is wedged; and adapter-reported state and provider detail from the heartbeat. Buttons invoke commands, toggle debug logging per instance at runtime, and kill a process so you can watch the supervisor restart it with backoff and then quarantine it. |
| **Events** | The push direction with its ack outcome and the host's reference. "Reject the next 3 events" on the Adapters page proves the ordering: a rejected event leaves the file in place, it is redelivered, and the dedupe key makes the second delivery recognisable. |
| **Logs & metrics** | Adapter log frames arriving as ordinary `ILogger` entries under `serverless.adapters.{id}`, and metric frames read back through a `MeterListener` on `System.Diagnostics.Metrics` — the same path any real exporter would use. |
| **Classic lifecycle** | The unchanged v1 path, running `SW.Serverless.Samples.Classic`. **Who am I?** returns the adapter's own pid: click it repeatedly and the classic pid changes every time while the resident pid beside it never moves. That spawn cost is what the pooled resident shape removes. Also covers the `{{expected}}` startup-value schema, typed in/out commands, a deliberate failure, and a timeout. |

## The transport

The adapter **dials the host** over a Unix domain socket (`/tmp/swsl-<pid>.sock`) or a named pipe
on Windows, and opens one bidirectional gRPC stream. A UDS is a filesystem path, not a network
address: **no port, no bind address, no firewall rule, and no long-lived network authentication
to configure.** The host validates a one-time handshake token on attach, so the endpoint is not
unauthenticated — it simply needs no credential management. The same contract binds to TCP + TLS for Kubernetes-orchestrated adapters, so both
modes share every line of code above the transport. See design section 15.

The host writes the socket path and a one-time token to the child's **stdin**, not `argv` — which
is also how broker credentials stop showing up in `ps aux`.

## The parts worth copying into a real provider

* **Ack ordering** (`FolderSource.DeliverAsync`) — the file is archived only *after* the host
  acknowledges. Crash in between and it is redelivered, which is exactly why every event carries
  a dedupe key. Copy this shape for a Kafka offset commit or a RabbitMQ `basic.ack`.
* **Status, not liveness** (`GetStatusAsync`) — separates *disconnected* from *idle* from
  *working*. A plain liveness probe conflates all three.
* **Bounded, droppable logs** — logging can never apply backpressure to the data path; events
  never drop.
* **Credit window** — `MaxInFlight` bounds unacknowledged events, so an adapter reading faster
  than the host persists cannot buffer its way to an OOM.

## Calling a resident adapter like a classic one

```csharp
// Classic — a process per call
using var scope = services.CreateScope();
var serverless = scope.ServiceProvider.GetRequiredService<IServerlessService>();
await serverless.StartAsync(adapterId, correlationId, settings);
var result = await serverless.InvokeAsync<ShipmentResult>("CreateShipment", request);

// Resident — a warm instance from the pool
await using var lease = await adapters.RentAsync(spec);
var result = await lease.InvokeAsync<ShipmentResult>("CreateShipment", request);
var logs   = await lease.InvokeAsync<CallLogResult>("GetLogs");
```

Same command name, same JSON payload, same result type. What changes is underneath: no process
spawn, no JIT, no storage metadata check, and a gRPC channel that stays open across calls instead
of being dialled and discarded each time.

**The session id is what makes the second form safe.** Traxis calls a command and then `GetLogs`,
and expects the command's upstream calls back — so both invocations have to land in the same
session. `lease.InvokeAsync` attaches the lease's session id; disposing the lease calls
`ResetAsync(sessionId)`, and the audit trail goes with it. Traxis's process-static `LogStore`
cannot do this: pool that process and one request's carrier calls appear in the next request's
audit record.

## A footgun the classic sample encodes

`Runner.Run(new Handler())` constructs the handler **before** `Runner` has parsed argv, so calling
`Runner.StartupValueOf(...)` from a constructor throws and the process dies before it can report
anything — the host only sees the stream close with "Received null data." Declare expectations in
the constructor; read values lazily, from the commands. `SW.Serverless.Samples.Classic` shows the
correct shape.

## Tests

```bash
dotnet test SW.Serverless.UnitTests/SW.Serverless.UnitTests.csproj
```

The suite runs against a local-filesystem cloud store, so it needs no credentials and no cloud
account. The RabbitMQ tests additionally start a broker with Testcontainers; **without Docker
they report Inconclusive rather than failing**, and `SWSL_SKIP_BROKER_TESTS=1` skips them
deliberately — so `dotnet test` is safe to run anywhere.

`RabbitAdapterTests` runs both broker adapters end to end: publish and confirm, publisher →
broker → consumer → host, `mandatory` returns, runtime prefetch and interval changes, purge,
staged test-connection, topology discovery, advertised commands, and heartbeat detail. The one
that matters most is `A_rejected_message_is_nacked_back_and_redelivered` — a host rejection
becomes `BasicNack(requeue: true)`, the message returns to the queue, and the redelivery carries
the same dedupe key so the host recognises it instead of persisting it twice.

`LargeFileAdapterTests` proves the streaming claim rather than asserting it: a 24 MB file through
a 64 KB chunker must not grow the adapter's working set by 24 MB. Measured in the sample web,
512 MB streamed in 2048 chunks while resident memory held flat at 62–64 MB.
`AdapterHostingTests` covers the DI container, including a regression for the environment-variable
trap above.

`CarrierAdapterTests` runs the carrier adapter against a real gRPC service on a loopback port:
DI-resolved upstream connection, the classic call shape, a rejection returned as a result rather
than thrown, retries on transient `Unavailable`, and the two that matter for pooling —
`GetLogs_after_a_command_sees_that_commands_calls` and
`One_leases_call_log_never_leaks_into_another`.

`ResidentAdapterTests` covers installation from storage, typed command results and
typed failures, push/ack, and three things worth calling out:

* **`A_timed_out_command_does_not_corrupt_the_next_call`** — the v1 regression. There a timed-out
  command left the child running and its late reply resolved the *next* caller's completion,
  which in Traxis reliably corrupted the `GetLogs` fetch issued right after it.
* **`Heartbeat_is_answered_while_a_command_is_running`** — proves the stream is multiplexed. If it
  were not, a slow command would starve the heartbeat and the supervisor would restart a healthy
  adapter.
* **`A_rejected_event_is_left_for_redelivery_and_then_deduplicated`** — ack ordering end to end.

## What is deliberately not here yet

The Kubernetes orchestrator and OTLP export. Both are called out in the design doc with the
phase they belong to.
