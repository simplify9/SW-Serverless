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

## Run the samples

Two hosts, for two kinds of look. Build the solution first — both package adapters from their
build output.

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
address: **no port, no bind address, no firewall rule, no auth token, no container network
config.** The same contract binds to TCP + TLS for Kubernetes-orchestrated adapters, so both
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
account. `ResidentAdapterTests` covers installation from storage, typed command results and
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
