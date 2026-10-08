
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
| `-v`, `--version` | `major`, `minor`, `patch`, or an explicit version such as `2.1.0` / `2.1.0-rc.1`. Omit to upload only `adapters/<id>`, as before (see Versions below) |
| `-k`, `--kind` | Roles the adapter serves (e.g. `handler,mapper`), when it does not declare them with `[AdapterKind]` or in `adapter.json` |

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
| `SWSL_PUBLISHED_BY` | Who is publishing, recorded in the catalog (then `GITHUB_ACTOR`, then the user name) |
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

**What is uploaded.** The entry assembly is the project's real `AssemblyName` (checked to exist in the publish output). Every package carries an `adapter.json` manifest (below), and every upload carries metadata: `EntryAssembly`, `Lang`, `Timestamp`, `Lifecycle` and `Kind` (read from the assembly), `Sha256` and `Hash` (hex SHA-256 of the zip; `Hash` is what hosts name the extraction folder after — S3 keeps using its ETag), and `Version` (empty for an unversioned upload). A file that cannot be read fails the packaging rather than being left out.

The tool exits `0` only when the upload completed; a bad command line, an invalid adapter id, an invalid manifest, a failed build or a failed upload all exit non-zero.

### Versions, promote and rollback

```bash
# Publish 1.4.0 and make it the one that runs (the default)
serverless -v 1.4.0 --notes "Retries on 503" ./MyAdapter/MyAdapter.csproj my.adapter

# Publish without switching: stage it, check it, then promote
serverless -v minor --no-promote ./MyAdapter/MyAdapter.csproj my.adapter
serverless promote my.adapter 1.5.0

# Roll back = promote an older version (no rebuild)
serverless promote my.adapter 1.4.0

# History, and taking a bad version out of use
serverless versions my.adapter
serverless withdraw my.adapter 1.5.0
```

The commands take the same storage flags, `-c` file and `SWSL_*` variables as publishing.

| Flag | Meaning |
|---|---|
| `--no-promote` | With `-v`: upload the version and record it, but leave what runs alone |
| `--notes "…"` | Release notes for this version (overrides `releaseNotes` in `adapter.json`) |
| `--published-by` | Recorded in the catalog; defaults to `SWSL_PUBLISHED_BY`, then `GITHUB_ACTOR`, then the user name |
| `--no-probe` | Do not start a classic adapter to ask which startup values it expects (see below) |

- **Publish with `-v`** uploads the package to `adapters-versions/<id>/<version>` (immutable: an existing version is refused) and, unless `--no-promote`, the same package to `adapters/<id>` — the key every host runs — then records the version in the catalog. `major`, `minor` and `patch` bump the highest released version already published (or start at `1.0.0`); pre-release and other non-version keys are ignored when finding it. An explicit version must be higher than every released version.
- **Publish without `-v`** works exactly as it always has: only `adapters/<id>` is written. The catalog's manifest follows it and its current version is cleared (an unversioned package is running); the version history is kept.
- **`promote <id> <version>`** downloads the version, checks its SHA-256 against the one recorded when it was published, and copies it over `adapters/<id>` with the full metadata (including anything else the version carried, such as `Protocol`). A withdrawn or unknown version is refused.
- **`withdraw <id> <version>`** marks the version withdrawn in the catalog: listed for history, refused by `promote`, not offered for pinning. The current version cannot be withdrawn — promote another first. The package itself stays, since a deployment may pin it.
- **`versions <id>`** lists versions, newest first, with publish time, publisher, a short SHA-256, `*` for the current one and `withdrawn`. For an adapter published before the catalog it lists the packages instead.

A host runs a specific version when asked for the adapter id `<id>/<version>`; a plain `<id>` runs whatever is current.

### Storage layout

| Key | What | Written by |
|---|---|---|
| `adapters/<id>` | The package that runs when no version is pinned, with the full metadata above | publish (unversioned, or `-v` without `--no-promote`), `promote` |
| `adapters-versions/<id>/<version>` | One immutable package per version | publish with `-v` |
| `adapters-catalog/<id>.json` | The catalog entry: current version, current manifest and SHA-256, the icon as a `data:` URI, and every version with its manifest, digest, time, publisher and withdrawn flag | every command but `versions` |
| `adapters/<id>/<version>` | Where installers before the catalog put versions. Still read (`versions`, `promote`, pinned refs), never written | — |

Versions and the catalog live **beside** `adapters/`, not under it: storage backed by a file system cannot keep `adapters/<id>` as a file and a folder at once, and hosts and Bitween builds older than the catalog treat every key under `adapters/` as an adapter. An adapter published by an older installer has no catalog entry until its next publish or promote, which create one from the packages and their metadata.

### The adapter manifest (`adapter.json`)

Put an `adapter.json` beside the project file to describe the adapter for a catalog or marketplace. It is optional: without one, the installer still writes a manifest into the package from what it can find out. The installer **merges** it: the author owns presentation, the installer owns the facts and overwrites them.

```json
{
  "displayName": "Acme Carrier",
  "summary": "Creates shipments and labels with Acme.",
  "description": "Longer **Markdown** description.",
  "publisher": { "name": "Simplify9", "url": "https://simplify9.com", "email": "support@simplify9.com" },
  "license": "MIT",
  "homepage": "https://example.com/acme",
  "repository": "https://github.com/example/acme-adapter",
  "icon": "assets/icon.png",
  "tags": [ "shipping", "labels" ],
  "categories": [ "Carriers" ],
  "kinds": [ "handler" ],
  "releaseNotes": "Retries on 503.",
  "compatibility": { "minHostVersion": "10.0.0", "minBitweenVersion": "9.2.0" },
  "properties": [
    { "name": "BaseUrl", "displayName": "API URL", "type": "text", "default": "https://api.acme.test", "group": "Connection" },
    { "name": "ApiKey", "type": "text", "required": true, "secret": true, "group": "Connection" },
    { "name": "Mode", "type": "select", "options": [ "test", "live" ], "default": "test" }
  ]
}
```

| Field | Owner | Meaning |
|---|---|---|
| `manifestVersion` | installer | `1`; never lowered when an author file from a newer tool says more |
| `id` | installer | The adapter id published |
| `version` | installer | The version published; absent for an unversioned upload |
| `displayName`, `summary`, `description` | author | Name, one line, longer Markdown |
| `publisher` | author | `{ name, url, email }` |
| `license`, `homepage`, `repository` | author | |
| `icon` | author | PNG, JPEG or SVG **inside the package**, relative to its root — include it in the publish output (`CopyToPublishDirectory`). A missing file fails the publish; one of 64 KB or less is also inlined in the catalog as a `data:` URI |
| `tags`, `categories` | author | Lists of strings |
| `releaseNotes` | author / `--notes` | What changed in this version |
| `kinds` | author | handler, mapper, receiver, validator… `--kind` wins over it; without either, the `[AdapterKind]` attributes in the assembly are used |
| `runtime` | installer | `dotnet` |
| `language` | author | Defaults from the project file: `csharp`, `fsharp` or `vb` |
| `entry` | installer | The entry assembly |
| `lifecycle` | installer | `classic` or `resident`, read from the assembly |
| `protocol` | installer | `{ "min": 2, "max": 2 }` for resident adapters; absent for classic |
| `sdkVersion` | installer | The `SimplyWorks.Serverless.Sdk` version it was built against |
| `publishedOn` | installer | When it was published |
| `compatibility` | author | `minHostVersion`, `minBitweenVersion` |
| `properties` | author / probe | What has to be configured: `name`, `displayName`, `description`, `type` (`text`, `multiline`, `number`, `boolean`, `select`, `json`), `required`, `secret`, `default`, `options` (for `select`), `group` |

Fields the installer does not know are kept and written back, so a manifest from a newer tool survives. The final manifest is validated (id and version format, paths inside the package, lifecycle, property names, types and duplicates) and a problem fails the publish before anything is uploaded.

**Properties.** If `adapter.json` declares `properties`, those are used. Otherwise, for a classic adapter, the installer starts the built adapter the way a host does and asks it for its expected startup values (`Runner.Expect`): `required` is the inverse of optional, `secret` is private, and `default` and `description` are carried over. This runs the adapter's constructor on the build machine; `--no-probe` skips it. A probe that fails is a warning, not a failed publish — the manifest then lists no properties. Resident adapters are not probed.

### Backward compatibility

Ten production deployments run hosts on `SimplyWorks.Serverless` 10.0.x and Bitween builds that predate the catalog. What this installer writes is held to what they read:

- `adapters/<id>` always holds the current package with the full metadata an old host needs (`EntryAssembly`, `Hash`, and everything it wrote before) — after a versioned publish, a promote, a rollback or an unversioned publish alike.
- Nothing is ever written under `adapters/` except `adapters/<id>`, so an old Bitween listing sees exactly the adapters that exist.
- Every existing command line works unchanged; without `-v` the upload is the same key with the same metadata, plus `adapter.json` inside the zip and an empty `Version`.
- A package without `adapter.json`, and an adapter with no catalog entry, still install, run, list, and can be promoted.

One limit: an old host asked for a pinned ref `<id>/<version>` looks only at `adapters/<id>/<version>`, so it cannot pin versions published by this installer (those are under `adapters-versions/`). Old hosts never used pinning; current hosts resolve pinned refs in both places.

`SW.Serverless.CompatibilityTests` holds these to account: a host built on the published 10.0.0 package (`SW.Serverless.Compat.OldHost`) installs and runs what this installer publishes, through promote and rollback; the current host runs packages made the old way and adapters built on the published 10.0.0 SDK, classic and resident; the old Bitween listing rule sees nothing new; and manifests and catalog entries with unknown fields round-trip.

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
