
# SW.Serverless

[![GitHub Actions](https://github.com/simplify9/SW-Serverless/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/simplify9/SW-Serverless/actions/workflows/nuget-publish.yml)
[![NuGet - SimplyWorks.Serverless](https://img.shields.io/nuget/v/SimplyWorks.Serverless.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless)
[![NuGet - SimplyWorks.Serverless.Sdk](https://img.shields.io/nuget/v/SimplyWorks.Serverless.Sdk.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless.Sdk)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**SW.Serverless** is an open-source .NET framework for building and running serverless adapters and services. It provides a runtime environment for executing .NET applications as serverless functions with process isolation and communication through stdin/stdout.

## NuGet Packages

| Package | Version | Downloads |
| ------- | ------- | --------- |
| `SimplyWorks.Serverless` | [![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Serverless.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless) | [![Downloads](https://img.shields.io/nuget/dt/SimplyWorks.Serverless.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless) |
| `SimplyWorks.Serverless.Sdk` | [![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Serverless.Sdk.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless.Sdk) | [![Downloads](https://img.shields.io/nuget/dt/SimplyWorks.Serverless.Sdk.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless.Sdk) |

## What's Included

- **SW.Serverless**: Core serverless service library with dependency injection extensions for ASP.NET Core
- **SW.Serverless.Sdk**: SDK for developing serverless adapters with the `Runner` class and logging utilities
- **SW.Serverless.SampleWeb**: Example ASP.NET Core web application showing integration
- **SW.Serverless.Installer**: Command-line tool for packaging and deploying adapters to cloud storage

## Quick Start

### Install NuGet Packages

```bash
dotnet add package SimplyWorks.Serverless
dotnet add package SimplyWorks.Serverless.Sdk
```

### Add to ASP.NET Core

```csharp
// In Startup.cs or Program.cs
services.AddServerless();
```

### Create an Adapter

```csharp
using SW.Serverless.Sdk;

class Handler
{
    public async Task<string> ProcessData(string input)
    {
        // Your serverless logic here
        return $"Processed: {input}";
    }
}

class Program
{
    static async Task Main(string[] args) => await Runner.Run(new Handler());
}
```

## Publishing Adapters (Installer)

`SW.Serverless.Installer` builds an adapter project (`dotnet publish -c Release`), zips the output and uploads it to the store the runtime installs adapters from. The executable is named `serverless`. From a checkout:

```bash
dotnet run --project SW.Serverless.Installer -- [options] <path/to/Adapter.csproj> <adapter-id>
```

```bash
# S3-compatible storage, next patch version
serverless -p s3 -a <access-key> -s <secret> -b <bucket> -u https://s3.example.com \
  -v patch ./MyAdapter/MyAdapter.csproj my.adapter

# Credentials from the environment (CI)
export SWSL_PROVIDER=s3 SWSL_ACCESS_KEY=... SWSL_SECRET_KEY=... SWSL_BUCKET=adapters SWSL_SERVICE_URL=https://s3.example.com
serverless -v minor ./MyAdapter/MyAdapter.csproj my.adapter

# Oracle or Google Cloud: settings from a config file
serverless -c cloudfiles.json -v 2.1.0 ./MyAdapter/MyAdapter.csproj my.adapter
```

| Flag | Meaning |
|---|---|
| `-p`, `--provider` | `s3`, `as` (Azure), `oc` (Oracle), `gc` (Google Cloud) or `local` (filesystem, for development) |
| `-a`, `--accesskey` | Access key |
| `-s`, `--secret` | Secret access key |
| `-b`, `--bucketname` | Bucket name |
| `-u`, `--url` | Service URL (for `local`, the storage folder) |
| `-c`, `--cloudfilesconfigpath` | JSON config file (see below) |
| `-v`, `--version` | `major`, `minor`, `patch`, or an explicit version such as `2.1.0` / `2.1.0-rc.1`. Omit for the legacy unversioned key `adapters/<id>` |
| `-k`, `--kind` | Roles the adapter serves (e.g. `handler,mapper`), when it does not declare them with `[AdapterKind]` |

The adapter id may contain only lowercase letters, digits, `.`, `_` and `-`, and must start with a letter or digit (uppercase input is lowercased).

**Where settings come from.** Each setting is taken from the first of: the command-line flag, the config file, then the environment variable.

| Environment variable | Setting |
|---|---|
| `SWSL_PROVIDER` | Provider |
| `SWSL_ACCESS_KEY` | Access key |
| `SWSL_SECRET_KEY` | Secret access key |
| `SWSL_BUCKET` | Bucket name |
| `SWSL_SERVICE_URL` | Service URL |
| `SWSL_REGION` | Region |
| `SWSL_GC_PROJECT_ID`, `SWSL_GC_PRIVATE_KEY_ID`, `SWSL_GC_PRIVATE_KEY`, `SWSL_GC_CLIENT_EMAIL`, `SWSL_GC_CLIENT_ID`, `SWSL_GC_CLIENT_X509_CERT_URL` | Google Cloud service account fields (`SWSL_GC_PRIVATE_KEY` may use literal `\n` for line breaks) |

The config file holds the same settings under `CloudFiles`. Oracle settings (`Region`, `TenantId`, `UserId`, `FingerPrint`, `RSAKey`, `NamespaceName`) are read only from the file:

```json
{
  "CloudFiles": {
    "Provider": "gc",
    "BucketName": "adapters",
    "ProjectId": "my-project",
    "PrivateKeyId": "…",
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\n…\n-----END PRIVATE KEY-----\n",
    "ClientEmail": "publisher@my-project.iam.gserviceaccount.com",
    "ClientId": "…"
  }
}
```

**Versioning.** With `-v`, the zip is uploaded to `adapters/<id>/<version>`. `major`, `minor` and `patch` bump the highest released version already there (or start at `1.0.0`); pre-release and other non-version keys are ignored when finding it. An explicit version must be higher than every released version and must not already exist.

**What is uploaded.** The entry assembly is the project's real `AssemblyName` (checked to exist in the publish output). Every upload carries metadata: `EntryAssembly`, `Lang`, `Timestamp`, `Lifecycle` and `Kind` (read from the assembly), and `Sha256` (hex SHA-256 of the zip). A file that cannot be read fails the packaging rather than being left out.

The tool exits `0` only when the upload completed; a bad command line, an invalid adapter id, a failed build or a failed upload all exit non-zero.

## Resident Adapters

A classic adapter is launched per invocation and exits when it returns. A **resident** adapter is
launched once and stays running, so it can hold state — an open broker connection, a pooled HTTP/2
channel, a warm cache — and push work into the host as well as receive it.

```mermaid
sequenceDiagram
    participant H as Host
    participant S as Cloud storage
    participant A as Adapter process

    H->>S: fetch + extract by adapter id
    H->>A: launch, with memory and CPU ceilings
    A->>H: dial back over UDS / named pipe (gRPC)
    A->>H: Hello — commands, capabilities, SDK version

    Note over H,A: process stays up

    loop while running
        H->>A: Invoke(command, argument)
        A-->>H: result
        A->>H: Publish(event)
        H-->>A: accepted / rejected
        H->>A: Ping
        A-->>H: status, counters, last error
    end

    H->>A: Stop(drain)
    A-->>H: finishes in flight, exits
```

The adapter dials **out** to the host over a Unix domain socket or a named pipe — nothing listens
on a TCP port, and the adapter needs no inbound reachability.

### Two directions

| | |
| --- | --- |
| **Host → adapter** | `InvokeAsync<T>("Command", argument)` — any public `Task`/`Task<T>` method on the handler, discovered by reflection. |
| **Adapter → host** | `context.PublishAsync(payload, dedupeKey, endpoint, …)` — the host persists, then answers accepted or rejected. The adapter acknowledges its source only after that. |

### Writing one

```csharp
using SW.Serverless.Sdk;
using SW.Serverless.Sdk.Resident;

class Handler : IResidentAdapter
{
    IAdapterContext _context;

    public Task StartAsync(IAdapterContext context, CancellationToken ct)
    {
        _context = context;                       // connect, subscribe, warm up
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<AdapterStatus> GetStatusAsync() =>
        Task.FromResult(new AdapterStatus { Connected = true, State = "Ready" });

    [AdapterCommand("What this command does.")]
    public Task<object> GetStats() => Task.FromResult<object>(new { ok = true });
}

class Program
{
    static Task Main() => Runner.RunResident(new Handler());
}
```

Host side:

```csharp
services.AddResidentAdapters<MyEventSink>(o =>
{
    o.HeartbeatInterval = TimeSpan.FromSeconds(15);
    o.SoftMemoryLimitBytes = 512L * 1024 * 1024;
});

var instance = await adapters.StartExclusiveAsync(new AdapterSpec
{
    AdapterId = "my.adapter",
    InstanceKey = "1",
    StartupValues = { ["Host"] = "broker.example.com" },
});
```

### Supervision

The host samples each adapter process on every heartbeat and acts on what it finds.

```mermaid
flowchart LR
    Sample[Heartbeat sample] --> Check{What did it find?}
    Check -- healthy --> Sample
    Check -- over soft memory or CPU --> Drain[Ask to drain, relaunch]
    Check -- over hard memory --> Kill[Kill process tree, relaunch]
    Check -- no answer --> Miss[Restart after N misses]
    Drain --> Quarantine[Repeated crashes: quarantine]
    Kill --> Quarantine
    Miss --> Quarantine
```

| Control | Effect |
| --- | --- |
| `SoftMemoryLimitBytes` | Asks the adapter to drain — in-flight work finishes, nothing is lost. |
| `HardMemoryLimitBytes` | Kills the process tree, and is applied to the runtime as `DOTNET_GCHeapHardLimit`. |
| `CpuPercentLimit` + `CpuLimitSamples` | Trips after N consecutive samples above the line, then asks to drain. The figure is a share of the whole machine, not of one core. |
| `UpdateLimitsAsync` | Changes ceilings on a running adapter. Soft and CPU apply on the next sample; a hard-memory change reports `RestartRequired`. |
| `RestartAsync` | Relaunches in place, keeping the instance key. |

### Discovery

Every public `Task`/`Task<T>` method on a handler is a command. The adapter reports them on attach,
with the shape needed to call one:

```csharp
foreach (var c in adapters.Describe().Single(h => h.InstanceKey == "1").CommandDetails)
    Console.WriteLine($"{c.Name}({c.ParameterType}) {c.ParameterSchema} — {c.Description}");
```

`ParameterSchema` lists a complex argument's properties as name → type, so a caller can build a form
for a command it has not seen before.

## Features

- **Process Isolation**: Each adapter runs in its own process with timeout management
- **Cloud Storage Integration**: Support for AWS S3, Azure Storage, and Oracle Cloud
- **Dependency Injection**: Built-in integration with ASP.NET Core DI container
- **Logging**: Structured logging support through `AdapterLogger`
- **Caching**: Adapter metadata caching with configurable duration
- **Version Management**: Semantic versioning support for adapter deployments

## Architecture

The framework consists of:

1. **ServerlessService**: Manages adapter lifecycle, installation, and invocation
2. **Runner**: Entry point for adapter applications with command processing
3. **Installer**: CLI tool for building and deploying adapter packages
4. **Cloud Storage**: Abstraction layer for different cloud storage providers

## Support

For issues, questions, or contributions, please visit the [GitHub repository](https://github.com/simplify9/SW-Serverless).
