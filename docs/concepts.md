# Concepts

This page explains the parts of SW-Serverless and the words the other pages use. It has no setup
steps; those are in [Hosting adapters](hosting.md) and [Writing adapters](writing-adapters.md).

## Host and adapter

A **host** is your application, with the `SimplyWorks.Serverless` library added. It knows how to
find adapters in storage, install them on its machine, start them, call them and stop them.

An **adapter** is a small program that the host runs as a **separate child process**. It exposes
**commands**: named operations that take at most one argument and return at most one result. The
host calls a command by name, for example `Greet` with the argument `"Ada"`, and gets back
`"Hello, Ada!"`.

Because an adapter is its own process:

- a crash, a hang or a memory leak in it does not take the host down;
- it can be written in a different language from the host;
- a new version can be installed while the host keeps running.

An adapter talks only to its host. It reads its configuration from the host, answers the host's
calls, and, if it is resident, hands events to the host. It does not need to know about the host's
database or internal services.

## Adapter id

Every adapter has an **id**, such as `acme.orders` or `greeter`. The id is how the host asks for
it and where it is stored. An id uses lowercase letters, digits, `.`, `_` and `-`, and starts with
a letter or digit. Uppercase is lowered when you publish.

## Package

A **package** is a zip file holding everything an adapter needs to run:

- the adapter's runnable files: a .NET publish output, Python or JavaScript files, or a binary;
- its dependencies (for Python and Node, copied into the package; see
  [Packaging and storage](packaging-and-storage.md));
- an `adapter.json` [manifest](#manifest) at the root;
- optionally, its **source**, under `source/` (see [Source in packages](#source-in-packages)).

`sw-serverless build` makes packages. A package runs on any host that has its runtime: nothing is
fetched when it is installed.

## Manifest

The **manifest** is the `adapter.json` file at the root of every package. It says how to run the
adapter (runtime, entry file, lifecycle, protocol), what it needs configured (its settings, called
properties in the manifest), what it is for (kinds and contracts) and how to present it (name,
summary, icon, publisher, tags).

You write a short `adapter.json` beside your project: an id, a version, a display name and so on.
`sw-serverless build` asks the built adapter to [describe itself](#describe) and writes the full
manifest into the package from both. Field reference: [The manifest](manifest.md).

## Storage, catalog and versions

Adapters are published to **cloud storage**: S3 or an S3-compatible store, Azure Blob Storage,
Google Cloud Storage, Oracle Object Storage, or a local folder for development. The host reads the
same storage. Inside it, everything lives under one root folder, `adapters` by default:

| Key | What it holds |
|---|---|
| `adapters/{id}` | The **current** package of a .NET adapter: the one that runs when no version is asked for. Older hosts read only this. |
| `adapters-versions/{id}/{version}` | One package per published **version**. Never overwritten. |
| `adapters-catalog/{id}.json` | The **catalog entry**: which version is current, every version with its manifest, digest, date and publisher, and the icon. |

A **version** is a semantic version such as `1.4.0` or `1.5.0-rc.1`. Publishing a version never
replaces an existing one. **Promoting** a version makes it current; rolling back is promoting an
older one. **Withdrawing** a version marks it as not to be used, without deleting it.

The **catalog** lets an application list adapters, with names, icons and settings, without opening
a single package. `AdapterCatalogStore` reads it.

A host asked for a plain id, such as `greeter`, runs the current version. Asked for
`greeter/1.4.0`, it runs that version. This is **pinning**.

Details: [Packaging and storage](packaging-and-storage.md).

## Lifecycles: classic and resident

An adapter has one of two **lifecycles**.

A **classic** adapter runs for one **session**. The host starts a process, calls one or more
commands, one at a time, and then stops the process. Use classic for request-and-reply work: each
use gets a fresh process with the settings for that use. In .NET a classic adapter calls
`Runner.Run`.

A **resident** adapter is started once and runs until it is stopped. It can keep connections open
(to a broker, a database, a partner's API), push **events** to the host, keep a small piece of
**state** with the host (such as a cursor), and report its **status** on every heartbeat. The host
supervises it: it restarts it if it crashes or stops answering, and stops restarting it
(**quarantines** it) after repeated crashes. In .NET a resident adapter calls `Runner.RunResident`;
in Python and Node an adapter class with a `start` method is resident.

The host can run resident instances in two ways:

- **exclusive**: exactly one process for a given key, for example one per broker connection;
- **pooled**: a few warm processes shared by many short uses, each use **renting** one and
  returning it. When it is returned, the adapter is told to forget that use (a **reset**).

## Protocols

The host and an adapter talk over one of two **protocols**.

**Protocol 1** is the original **classic text protocol**, used only by .NET adapters that call
`Runner.Run`. Commands and results are single lines on the process's standard input and output.

**Protocol 2** is **gRPC over a local socket**. The host writes a one-line JSON **handshake** on
the adapter's standard input: a socket path (a Unix domain socket on Linux and macOS, a named pipe
on Windows) and a one-time token. The adapter connects to that socket and opens one gRPC stream,
over which every command, result, event, log line and heartbeat travels. Nothing listens on a
network port.

Which protocol is used:

| Adapter | Protocol |
|---|---|
| .NET, `Runner.Run` (classic) | 1 |
| .NET, `Runner.RunResident` (resident) | 2 |
| .NET, `Runner.RunResident` with `"lifecycle": "classic"` in its `adapter.json` | 2, run as a classic session |
| Python, Node, `exec` | 2, classic or resident |

Classic adapters on protocol 2 are still called with `IServerlessService`, exactly like protocol 1
ones; the host runs them on the resident machinery underneath. Details: [The protocol](protocol.md).

## Runtimes

The manifest's `runtime` says how the host starts the adapter. The host maps each name to a
launcher it controls; a package cannot name an arbitrary program.

| Runtime | Started as | Needs on the host |
|---|---|---|
| `dotnet` (the default) | `dotnet <entry>.dll` | the .NET runtime the adapter targets |
| `python` | `python3 -u <entry>` | Python 3.12 or later, unless the manifest asks otherwise |
| `node` | `node <entry>` | Node 22 or later, unless the manifest asks otherwise |
| `exec` | the entry file itself | nothing: the package carries a self-contained binary |

The host checks that a Python or Node runtime is present, and at a version the manifest accepts,
before starting anything. It also refuses a package built for other platforms (`linux-x64`,
`osx-arm64` and so on) than its own. Where to find `python3`, `node` and `dotnet` is configurable;
see [Hosting adapters](hosting.md#adapterruntimeoptions-addadapterruntimes).

## Settings

An adapter declares the **settings** it reads: their names, whether each is required, whether it is
secret, a default and a description. In .NET this is `Runner.Expect`; in Python `sw.expect`; in
Node `expect`. The old .NET name for settings is **startup values**, and the manifest calls them
**properties**.

The host passes the values when it starts the adapter. Commands read them with
`Runner.StartupValueOf`, `sw.value_of` or `valueOf`.

Declaring settings in code means the adapter itself is the one place that says what it needs.
`sw-serverless build` writes the declarations into the manifest, so an application can show a form
for an adapter's settings without starting it. The conformance kit checks that the two agree.

A resident instance can also receive **per-call values** with each command. A command reads them
through the same functions; a per-call value wins over a startup value of the same name.

The host always adds a `CorrelationId` value for classic sessions, from the `correlationId` it was
given.

## Kinds and contracts

A **kind** is a label for what an adapter is for, such as `processor` or `source`. SW-Serverless
does not interpret kinds; the application decides what they mean.

A **contract** is a document an application writes to say what its adapters must do: for each kind,
the commands it calls, the shape of their arguments and results (as JSON Schema), example inputs,
which kinds are called as an ordered session, and which commands are destructive. An adapter
declares the contracts it implements, with a version (`orders` version 1), and the kinds it
implements.

SW-Serverless ships no contract of its own. The conformance kit (`sw-serverless test`) checks an
adapter against any contract it is given. See [Contracts](contracts.md).

## Describe

Every SDK answers the `--describe` command-line flag: started with it, the adapter prints one JSON
document with its SDK, lifecycle, protocol, settings, commands (with JSON Schemas of their
arguments and results), kinds and contracts, and exits. The tools use this to learn about an
adapter in any language without reading its code. The format is in
[The protocol](protocol.md#describe).

## Source in packages

By default `sw-serverless build` puts the adapter's source in the package under `source/`, and
lists every source file with its SHA-256 in the manifest. This lets anyone holding a published
version read, compare, audit or rebuild it. The build leaves out build output, dependencies,
editor folders and files that usually hold secrets, follows `.gitignore` and `.serverlessignore`,
and refuses to build if a source file looks like it contains a secret.

Hosts do not unpack `source/` when they install a package. `--no-source` leaves the source out.

## Compatibility

Hosts and SDKs of different ages work together. Packages without a manifest, hosts from before
versions and the catalog, and adapters built on older SDKs all keep working. See
[Compatibility](compatibility.md).
