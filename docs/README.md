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

```bash
dotnet build SW.Serverless.sln
dotnet run --project SW.Serverless.Samples.Host
```

You should see both adapters attach, a burst of commands, then events arriving forever. Drop a
`*.json` file into the inbox the host prints and it comes back as an `EVENT` line within a poll.

| Sample | What it is for |
|---|---|
| `SW.Serverless.Samples.Ticker` | Smallest resident adapter. Proves attach, push/ack, heartbeat, runtime reconfiguration and typed command errors. No dependencies. |
| `SW.Serverless.Samples.FolderSource` | The reference **data source** shape — ingress, egress, topology, discovery, test-connection, real status — using a folder instead of a broker. A RabbitMQ or Kafka adapter is this class with a different client. |
| `SW.Serverless.Samples.Host` | Stands in for Bitween. Implements `IAdapterEventSink`, starts both adapters, prints events and heartbeats. |

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

## What is deliberately not here yet

The S3 locator for resident specs (`AdapterSpec.EntryAssemblyPath` is set directly in the
samples), the Kubernetes orchestrator, and OTLP export. Each is called out in the design doc with
the phase it belongs to.
