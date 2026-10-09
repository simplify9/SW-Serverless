# Hosting adapters

This page is for developers adding SW-Serverless to an application that runs adapters. Read
[Concepts](concepts.md) first if the words host, session, resident or protocol are new.

- [Packages to add](#packages-to-add)
- [Registering the host](#registering-the-host)
- [Options](#options)
- [Storage](#storage)
- [Classic sessions: IServerlessService](#classic-sessions-iserverlessservice)
- [Resident adapters: IResidentAdapterHost](#resident-adapters-iresidentadapterhost)
- [The event sink](#the-event-sink)
- [The state store](#the-state-store)
- [Logs and metrics](#logs-and-metrics)
- [Pinning versions](#pinning-versions)
- [How installation works](#how-installation-works)
- [Limits and supervision](#limits-and-supervision)
- [What the host machine needs](#what-the-host-machine-needs)
- [Security](#security)

## Packages to add

```sh
dotnet add package SimplyWorks.Serverless
```

and one storage provider, from the `SimplyWorks.CloudFiles` family:

| Provider | Package | Registration |
|---|---|---|
| S3 and S3-compatible | `SimplyWorks.CloudFiles.S3.Extensions` | `AddS3CloudFiles` |
| Azure Blob Storage | `SimplyWorks.CloudFiles.AS.Extensions` | `AddAsCloudFiles` |
| Google Cloud Storage | `SimplyWorks.CloudFiles.GC.Extensions` | `AddGoogleCloudFiles` |
| Oracle Object Storage | `SimplyWorks.CloudFiles.OC.Extensions` | `AddOracleCloudFiles` |
| A local folder (development) | `SimplyWorks.CloudFiles.LocalTests.Extensions` | `AddLocalTestsCloudFiles` |

The library targets .NET 10 and uses ASP.NET Core (it serves the gRPC endpoint adapters connect
to), so the application must be able to reference `Microsoft.AspNetCore.App`.

## Registering the host

```csharp
using SW.CloudFiles.Extensions;
using SW.Serverless;
using SW.Serverless.Resident;

// 1. Where adapters are published.
services.AddS3CloudFiles(o =>
{
    o.BucketName = "my-adapters";
    o.AccessKeyId = configuration["Adapters:AccessKey"];
    o.SecretAccessKey = configuration["Adapters:SecretKey"];
    o.ServiceUrl = "https://s3.eu-central-1.amazonaws.com";
});

// 2. Optional: where python3, node and dotnet are. Must come before AddServerless.
services.AddAdapterRuntimes(o => o.PythonExecutable = "/usr/bin/python3.12");

// 3. Classic sessions, and the installer both lifecycles share.
services.AddServerless(o =>
{
    o.AdapterLocalPath = "/var/lib/myapp/adapters";
    o.CommandTimeout = 60;
});

// 4. Resident adapters, and protocol 2 sessions (every Python, Node and exec adapter).
services.AddResidentAdapters<OrderEventSink, OrderStateStore>(o =>
{
    o.HeartbeatInterval = TimeSpan.FromSeconds(15);
    o.SoftMemoryLimitBytes = 512L * 1024 * 1024;
});
```

What each call registers:

| Call | Registers |
|---|---|
| `AddServerless(Action<ServerlessOptions>)` | `IServerlessService` (transient), `ServerlessOptions`, the shared `AdapterInstaller` and `AdapterRuntimes`, a memory cache. |
| `AddAdapterRuntimes(Action<AdapterRuntimeOptions>)` | Where the host finds `python3`, `node` and `dotnet`. The other calls register defaults only if none is registered, so call this one first. |
| `AddResidentAdapters<TSink>(Action<ResidentOptions>)` | `IResidentAdapterHost` (a singleton and a hosted service), your `IAdapterEventSink`, and an in-memory `IAdapterStateStore`. |
| `AddResidentAdapters<TSink, TStateStore>(...)` | The same, with your own `IAdapterStateStore`. |

Rules to know:

- `AddResidentAdapters` works on its own when every adapter is given by path
  (`AdapterSpec.EntryAssemblyPath`). To install adapters from storage by id, register
  `AddServerless` and an `ICloudFilesService` as well: the resident host installs through the same
  installer, which needs both, and says so if they are missing when it first needs them.
- Register `AddResidentAdapters` whenever you run anything other than .NET classic adapters. A
  Python, Node or `exec` adapter, and a .NET adapter on protocol 2, is run on the resident host even
  when it is called as a classic session. Without it, `StartAsync` fails with a message saying so.
- The resident host is an `IHostedService`. It opens its socket when the application's host starts,
  so use it from a running host (`WebApplication`, `Host.CreateDefaultBuilder`, and so on).

## Options

### ServerlessOptions (`AddServerless`)

| Option | Default | Meaning |
|---|---|---|
| `AdapterRemotePath` | `"adapters"` | The root folder in storage. Must match the root adapters were published under; the CLI always publishes under `adapters`. |
| `AdapterLocalPath` | `"./adapters"` | Where packages are unpacked on this machine, one folder per package. |
| `AdapterMetadataCacheDuration` | `5` | Minutes the host remembers which package an adapter id resolves to. A newly promoted version is picked up once this expires. |
| `CommandTimeout` | `30` | Seconds a classic session's command may take, unless the call passes its own. |
| `IdleTimeout` | `300` | Seconds a protocol 1 (.NET classic) adapter waits for its next command before it exits. |

### ResidentOptions (`AddResidentAdapters`)

| Option | Default | Meaning |
|---|---|---|
| `SocketPath` | `/tmp/swsl-{pid}.sock` | The Unix domain socket adapters connect to (Linux, macOS). macOS limits the path to about 104 bytes. |
| `PipeName` | `swsl-{pid}` | The named pipe adapters connect to (Windows). |
| `HandshakeTimeout` | 30 s | How long a started adapter has to connect and say Hello. |
| `InvokeTimeout` | 300 s | Default timeout for a command on a resident instance. |
| `HeartbeatInterval` | 15 s | How often each instance is pinged and its process sampled. |
| `MissedHeartbeatsBeforeRestart` | `3` | Unanswered pings in a row before the process is killed and restarted. |
| `MaxInFlight` | `16` | How many events one instance may have waiting for the event sink. |
| `CrashLoopThreshold` / `CrashLoopWindow` | `5` / 5 min | Crashes within the window before the instance is quarantined (no more restarts). |
| `SoftMemoryLimitBytes` | `0` (off) | Memory above which the adapter is asked to drain and is then restarted. |
| `HardMemoryLimitBytes` | `0` (off) | Memory above which the process is killed. Also given to the runtime as a heap limit. |
| `CpuPercentLimit` | `0` (off) | Sustained CPU, as a share of the whole machine, above which the adapter is asked to drain. |
| `CpuLimitSamples` | `4` | Heartbeats in a row above `CpuPercentLimit` before it trips. |
| `DrainDeadline` | 60 s | How long an adapter asked to drain has to exit before it is killed. |
| `IdleTimeout` | 10 min | How long a pooled instance may sit unused before it is stopped. `TimeSpan.Zero` disables this. |
| `DiagnosticBufferLines` | `200` | Lines of an adapter's stdout and stderr kept for error messages. |
| `UseWorkstationGc` | `true` | Start .NET adapters with workstation GC, which uses less memory than server GC. |

### AdapterRuntimeOptions (`AddAdapterRuntimes`)

| Option | Default |
|---|---|
| `PythonExecutable` | `"python3"` |
| `NodeExecutable` | `"node"` |
| `DotnetExecutable` | `"dotnet"` |

Each is looked up on the `PATH` unless you give a full path. `DotnetExecutable` is used for .NET
adapters on protocol 2; protocol 1 sessions always start `dotnet` from the `PATH`.

## Storage

The host reads adapters from the `ICloudFilesService` you register, under `AdapterRemotePath`. Use
the same bucket and credentials (read access is enough) that adapters are published to.

For development, publish to a folder and point the host at it:

```sh
sw-serverless publish bin/serverless/greeter-0.1.0.zip -p local -b adapters-dev -u /tmp/swsl-store
```

```csharp
services.AddLocalTestsCloudFiles(o =>
{
    o.BucketName = "adapters-dev";
    o.StoragePath = "/tmp/swsl-store";
});
```

The layout inside storage is described in [Packaging and storage](packaging-and-storage.md).

## Classic sessions: IServerlessService

`IServerlessService` (namespace `SW.PrimitiveTypes`) runs one **session**: it starts the adapter's
process, calls commands one at a time, and stops the process when it is disposed. Resolve one per
session from a scope, and let the scope end the session.

```csharp
using Microsoft.Extensions.DependencyInjection;
using SW.PrimitiveTypes;

public class OrderService(IServiceScopeFactory scopes)
{
    public async Task<Receipt> ProcessAsync(Order order, string partnerKey)
    {
        using var scope = scopes.CreateScope();
        var serverless = scope.ServiceProvider.GetRequiredService<IServerlessService>();

        await serverless.StartAsync(
            adapterId: "acme.orders",
            correlationId: order.Id,
            startupValues: new Dictionary<string, string> { ["ApiKey"] = partnerKey });

        var receipt = await serverless.InvokeAsync<Receipt>("Process", order);
        var log = await serverless.InvokeAsync<string>("GetLog", null);   // same process, same session
        return receipt;
    }   // disposing the scope stops the adapter
}
```

| Member | What it does |
|---|---|
| `StartAsync(adapterId, correlationId, startupValues = null)` | Installs the adapter if needed and starts it with these settings. `adapterId` may pin a version: `"acme.orders/1.4.0"`. The host adds `CorrelationId` to the values. |
| `StartAsync(adapterId, correlationId, adapterPath, startupValues = null)` | Starts a .NET protocol 1 adapter from a `.dll` on disk, without storage. For development and tests. |
| `InvokeAsync<TResult>(command, input, commandTimeout = 0)` | Calls a command and converts its result to `TResult`. `commandTimeout` is in seconds; `0` uses `CommandTimeout`. |
| `InvokeAsync(command, input, commandTimeout = 0)` | Calls a command that returns nothing. |
| `GetExpectedStartupValues()` | The settings the adapter declares: name, `Optional`, `Default`, `Type`, `Private`, `Description`. |
| `Dispose()` | Stops the adapter. `ServerlessService` implements `IDisposable`; the scope calls it. |

Rules of a session:

- One command at a time. Wait for each call to finish before the next. Use one session per
  concurrent use.
- How `input` is sent: a `string` as its raw text, `null` as no argument, anything else as JSON. A
  `string` result is the raw text; anything else is read as JSON into `TResult`. See
  [encoding](writing-adapters.md#encoding).
- A failing command throws. On protocol 2 the exception is `AdapterInvocationException` (namespace
  `SW.Serverless.Resident`) with `AdapterExceptionType` (the adapter's error type), `Message` and
  `Detail` (its stack trace). On protocol 1 it is an `Exception` whose message carries the
  adapter's exception text. The session stays usable after a failed command.
- A command that runs past its timeout throws `TimeoutException`. On protocol 1 the process is
  then killed and the session cannot be used again. On protocol 2 the adapter is told the call was
  cancelled, and the session stays usable.

## Resident adapters: IResidentAdapterHost

`IResidentAdapterHost` (namespace `SW.Serverless.Resident`) is a singleton that owns every
long-running adapter process on this machine.

### Exclusive instances

An exclusive instance is one process per `(AdapterId, InstanceKey)`. Starting a key that is
already running returns the running instance.

```csharp
var instance = await adapters.StartExclusiveAsync(new AdapterSpec
{
    AdapterId = "acme.queue-reader",
    InstanceKey = "subscription-42",
    StartupValues = { ["Host"] = "broker.internal", ["Queue"] = "orders" },
    SoftMemoryLimitBytes = 256L * 1024 * 1024,
});

var stats = await instance.InvokeAsync<QueueStats>("GetStats");
await instance.InvokeAsync<object>("SetPrefetch", 50);

await adapters.StopAsync("acme.queue-reader", "subscription-42", drain: true);
```

`AdapterSpec`:

| Property | Meaning |
|---|---|
| `AdapterId` | The id, optionally pinned: `"acme.queue-reader/2.1.0"`. |
| `InstanceKey` | The exclusive instance's key. Left empty for pooled rentals. |
| `StartupValues` | The adapter's settings. Sent over the socket, never on the command line. |
| `AdapterValues` | Extra values the adapter can read (in .NET, `IAdapterContext.AdapterValues`). Merged over the package's storage metadata. Pools read `PoolSize` and `IdleTimeoutSeconds` from here. |
| `EntryAssemblyPath` | Start this file instead of installing from storage. For development. |
| `Runtime` | With `EntryAssemblyPath`: `dotnet`, `python`, `node` or `exec`. Filled in from the manifest when installing from storage. |
| `PoolKey` | For pooled rentals: which pool. Defaults to the id plus a hash of `StartupValues`, so different settings never share a process. |
| `SoftMemoryLimitBytes`, `HardMemoryLimitBytes`, `CpuPercentLimit`, `CpuLimitSamples` | Limits for this instance; `0` uses the host default. |

`IResidentAdapterHost`:

| Member | What it does |
|---|---|
| `StartExclusiveAsync(spec)` | Starts the instance, or returns it if it is already running. Waits until the adapter has connected. |
| `StopAsync(adapterId, instanceKey, drain = true)` | Asks the adapter to stop (with `drain`, to finish what it is doing first: up to 30 s, otherwise 5 s), then ends the process. |
| `RestartAsync(adapterId, instanceKey, drain = true)` | Stops and starts it again under the same key. |
| `UpdateLimitsAsync(adapterId, instanceKey, ResourceLimits)` | Changes its limits while it runs. Soft memory and CPU limits apply at once; a hard memory change returns `RestartRequired = true`, because the runtime reads it at launch. |
| `RentAsync(spec)` | Rents a pooled instance. See below. |
| `Get(adapterId, instanceKey)`, `List()` | The running `ResidentAdapterInstance` objects. |
| `Describe()` | An `InstanceHealth` for each instance (see [Limits and supervision](#limits-and-supervision)). |

`ResidentAdapterInstance`:

| Member | What it does |
|---|---|
| `InvokeAsync<TResult>(command, input = null, timeoutSeconds = 0, cancellationToken = default, sessionId = null, properties = null)` | Calls a command. Several calls may run at once. `properties` are per-call values the adapter reads like settings. `0` seconds uses `InvokeTimeout`. |
| `InvokeAsync(command, byte[] payload, ...)` | The same with raw bytes in and out. |
| `PingAsync(timeout)` | Pings it now and returns its status (`Pong`). |
| `ResetAsync(sessionId)` | Tells it to forget a session. |
| `SetLogLevelAsync(LogLevel)` | Changes the lowest level it sends logs at. |
| `State`, `Commands`, `CommandDetails`, `Settings`, `Kinds`, `Contracts`, `SdkVersion`, `SdkLanguage`, `ProtocolVersion` | What it said about itself when it connected. |

### Pooled instances

A pool keeps a few warm processes of one adapter with one set of settings, and hands them out for
short uses. It replaces starting a process per use.

```csharp
await using (var lease = await adapters.RentAsync(new AdapterSpec
{
    AdapterId = "acme.pricing",
    StartupValues = { ["Region"] = "eu" },
    AdapterValues = { ["PoolSize"] = "4" },
}))
{
    var quote = await lease.InvokeAsync<Quote>("Price", basket);
    var audit = await lease.InvokeAsync<string>("GetAudit");   // same session as the call above
}   // returned to the pool; the adapter is told to reset this session
```

- Calls through a lease carry the lease's session id. Returning the lease sends a reset for that
  session, and waits for the adapter to confirm it before the process is rented again. A pooled
  adapter must forget per-session state on reset: in .NET implement `IResettable`, in Python and
  Node a `reset(session_id)` method.
- `PoolSize` (in `AdapterValues`, default 4, at most 64) caps the processes in one pool.
  `IdleTimeoutSeconds` overrides `ResidentOptions.IdleTimeout` for this adapter.
- Every distinct set of `StartupValues` is its own pool, unless you set `PoolKey`.

## The event sink

A resident adapter pushes work into the host by publishing **events**: a message read from a queue,
a file found in a folder. You implement `IAdapterEventSink` to receive them.

```csharp
public class OrderEventSink(OrdersDb db) : IAdapterEventSink
{
    public async Task<EventOutcome> OnEventAsync(InboundEvent e, CancellationToken cancellationToken)
    {
        if (await db.HasSeenAsync(e.DedupeKey, cancellationToken))
            return EventOutcome.Ok(reference: "duplicate");

        var id = await db.SaveIncomingAsync(e.AdapterId, e.Endpoint, e.ContentType, e.Payload, cancellationToken);
        return EventOutcome.Ok(reference: id.ToString());
    }
}
```

`InboundEvent` has `AdapterId`, `InstanceKey`, `Endpoint` (where it came from: a queue, a topic, a
folder), `DedupeKey`, `ContentType`, `Payload` (bytes), `Headers` and `Traceparent`.

How it works:

1. The adapter publishes an event and waits.
2. The host calls `OnEventAsync`. Return `EventOutcome.Ok(reference)` once the event is stored
   durably, or `EventOutcome.Rejected(error)` to refuse it. An exception is treated as a rejection.
3. The adapter receives the outcome. Only after an accepted outcome should it acknowledge its source
   (delete the file, ack the message). After a rejection or a crash, the source still has the
   message and the adapter will deliver it again.

Delivery is therefore at least once. Use `DedupeKey` to recognise a repeat. At most `MaxInFlight`
events per instance wait for the sink at once.

## The state store

A resident adapter may keep a small piece of durable state with the host: the cursor of a polling
reader, the time of its last run. The adapter cannot keep this itself, because it is restarted, may
run on another machine next time, and a pooled one is not the same process twice.

```csharp
public class OrderStateStore(OrdersDb db) : IAdapterStateStore
{
    public Task<string> GetAsync(AdapterStateKey key, CancellationToken cancellationToken) =>
        db.GetAdapterStateAsync(key.AdapterId, key.InstanceKey, key.Name, cancellationToken);   // null when absent

    public Task SetAsync(AdapterStateKey key, string value, CancellationToken cancellationToken) =>
        value == null
            ? db.DeleteAdapterStateAsync(key.AdapterId, key.InstanceKey, key.Name, cancellationToken)
            : db.SaveAdapterStateAsync(key.AdapterId, key.InstanceKey, key.Name, value, cancellationToken);
}
```

- Keys are scoped by adapter id, instance key and a name the adapter chooses.
- `SetAsync` must be durable before it returns. A `null` value deletes the entry.
- Values are meant to be small. You may refuse a large one by throwing; the adapter receives the
  error.
- The default `InMemoryAdapterStateStore` keeps state in memory, per process. Replace it in any real
  deployment with `AddResidentAdapters<TSink, TStateStore>`.

## Logs and metrics

- Adapter log lines arrive as ordinary `ILogger` entries in the category
  `serverless.adapters.{adapter id}`. Configure their level and output like any other logs. Use
  `instance.SetLogLevelAsync` to change what a resident adapter sends.
- Metrics that resident adapters record are published as counters on the
  `System.Diagnostics.Metrics` meter named `SW.Serverless.Adapters`, tagged `adapter.id` and
  `adapter.instance`. Export them as you export other metrics. The host keeps at most 200 metric
  names and 10 tags per metric.

## Pinning versions

Pass `"{id}/{version}"` wherever an adapter id is taken:

```csharp
await serverless.StartAsync("acme.orders/1.4.0", correlationId);
await adapters.StartExclusiveAsync(new AdapterSpec { AdapterId = "acme.orders/1.4.0", InstanceKey = "main" });
```

A plain id runs the current version. A pinned version is fetched from `adapters-versions/` (or from
where older publishing tools put versions). Withdrawing a version does not stop a host that pins it;
the package stays in storage.

## How installation works

When the host is asked for an adapter id it:

1. Resolves the id to a package in storage: `adapters/{id}`, or for an adapter with no package
   there (Python, Node, `exec`), the catalog's current version; or the pinned version. The answer
   is cached for `AdapterMetadataCacheDuration` minutes.
2. Downloads the zip to a temporary file and unpacks it into `AdapterLocalPath/{package hash}`, into
   a temporary folder first and then renamed into place, so a half-unpacked package is never used.
   `source/` is not unpacked. An entry that would land outside the folder fails the install, and so
   does a package that unpacks to more than 4 GB (`AdapterInstaller.MaxExtractedBytes`).
3. Reads `adapter.json`, and refuses to start the adapter if this host does not have its runtime,
   has the runtime at a version outside the manifest's `runtimeVersion`, is not one of its
   `platforms`, or is older than its `compatibility.minHostVersion`. The error says which.
4. Deletes older unpacked versions of the same adapter that no running process uses.

A package with no `adapter.json` (from before manifests) runs as a .NET adapter, as it always did.

## Limits and supervision

Supervision applies to processes on the resident host: resident instances, and classic sessions
on protocol 2 (Python, Node, `exec`, and .NET adapters that opt in). Protocol 1 .NET sessions have
the command timeout and the idle timeout, and nothing else.

On every heartbeat the host samples each process's memory (resident set size) and CPU, and pings
the adapter:

| Finding | What the host does |
|---|---|
| Memory above the soft limit | Asks the adapter to drain (finish in-flight work and exit), then restarts it. Killed if it hasn't exited after `DrainDeadline`. |
| Memory above the hard limit | Kills the process tree and restarts it. |
| CPU above `CpuPercentLimit` for `CpuLimitSamples` heartbeats | Asks it to drain, then restarts it. There is no hard CPU kill. |
| `MissedHeartbeatsBeforeRestart` pings unanswered | Kills it and restarts it. |
| The process exits unasked | Restarts it after a backoff. After `CrashLoopThreshold` crashes in `CrashLoopWindow`, quarantines it: it stays stopped until started again. |

Notes:

- The CPU figure is a share of the whole machine. On a 16-core machine one busy core is about 6%.
- The hard memory limit is also given to the runtime: `DOTNET_GCHeapHardLimit` for .NET and
  `--max-old-space-size` for Node, so an allocation past it fails inside the adapter. Python has no
  such setting; only the host's kill applies.
- On Linux the host sets each adapter's `oom_score_adj` to 500, so under memory pressure the kernel
  kills an adapter before the host.
- SW-Serverless does not set operating-system limits such as cgroups. For a hard guarantee, run the
  host in a container with its own limits.

`Describe()` reports, per instance, what the host observes (`ProcessId`, `WorkingSetBytes`,
`CpuPercent`, `ThreadCount`, `Uptime`, `RestartCount`, `MissedHeartbeats`, `Quarantined`,
`DrainRequested`, `LastHeartbeatOn`, `IdleSince`), what the adapter reports in its status
(`Connected`, `ReportedState`, `LastMessageOn`, `InFlight`, `LastError`, `Details`), what it
declared when it connected (commands, settings, kinds, contracts, SDK), and the last lines of its
output (`Diagnostics`).

## What the host machine needs

| Adapters you run | The host machine needs |
|---|---|
| .NET | `dotnet` and the .NET runtime the adapters target. |
| Python | `python3`, version 3.12 or later (or what the adapter's `runtimeVersion` asks for). Nothing from PyPI: packages carry their dependencies. |
| JavaScript, TypeScript | `node`, version 22 or later (or what the adapter's `runtimeVersion` asks for). Nothing from npm. |
| `exec` | Nothing; the package is the program. It must be built for the host's platform. |

The Python and Node SDKs connect over a Unix domain socket only, so Python and Node adapters run
on Linux and macOS hosts, not Windows. .NET adapters run on all three.

## Security

- **An adapter is not sandboxed.** It runs as the same operating-system user as the host, with the
  same file and network access. Run only adapters you trust, or run the host in a container with
  the access the adapters should have.
- **No network port.** Adapters connect to the host over a Unix domain socket or a named pipe. On
  Unix the socket file is made readable and writable by its owner only, and its folder, unless it
  is `/tmp`, by its owner only.
- **A one-time token.** The host writes the socket path and a random token on the adapter's
  standard input. The adapter must present the token in its first message; an unknown or reused
  token is refused.
- **Settings stay off the command line.** On protocol 2, settings travel over the socket. On
  protocol 1 they are written on the adapter's standard input when its SDK is 10.1.0 or later
  (known from the manifest's `sdkVersion`). Adapters built on older SDKs receive them as
  base64-encoded command-line arguments, which other processes on the machine can read; rebuild
  them on a current SDK.
- **Runtimes are names, not paths.** A manifest says `python` or `node`; the host decides which
  program that means. A package's entry, and every file in it, must lie inside the package.
- **Secrets in source.** `sw-serverless build` refuses to package source that looks like it holds a
  key or password. Mark secret settings as secret, so applications mask them.
