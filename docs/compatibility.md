# Compatibility

SW-Serverless runs in deployments where hosts, adapters and publishing tools are upgraded at
different times. This page says what keeps working across versions, and how the protocol and
file formats are versioned so that it stays that way.

- [Versions of the parts](#versions-of-the-parts)
- [Current hosts and older adapters](#current-hosts-and-older-adapters)
- [Older hosts and current packages](#older-hosts-and-current-packages)
- [The protocol](#the-protocol)
- [Manifests, catalog entries and descriptions](#manifests-catalog-entries-and-descriptions)
- [Storage](#storage)
- [The CLI](#the-cli)
- [Requiring a newer host or application](#requiring-a-newer-host-or-application)
- [How this is tested](#how-this-is-tested)

## Versions of the parts

| Part | Version line | Notes |
|---|---|---|
| Host, `SimplyWorks.Serverless` | 10.x | `HostInfo.Version` is the host's version, compared with an adapter's `minHostVersion`. |
| .NET SDK, `SimplyWorks.Serverless.Sdk` | 10.x | 10.1.0 added `--describe` (needed by `sw-serverless build`) and reading settings from standard input. |
| Python SDK `sw-serverless`, Node SDK `@simplyworks/sw-serverless` | 10.2.2 | Copied into each package by `sw-serverless build`. |
| Protocol | 1 and 2 | 1 is the .NET classic text protocol; 2 is gRPC. Hosts speak 2 to 2. |
| Manifest (`adapter.json`) | `manifestVersion` 1 | |
| Catalog entry | `catalogVersion` 1 | |
| Describe output | `describeVersion` 1 | |

## Current hosts and older adapters

A current host runs:

- **packages without `adapter.json`**, published before manifests: as .NET adapters, from their
  storage metadata, as before;
- **.NET classic adapters on SDKs before 10.1.0**: they receive their settings as command-line
  arguments, as they expect. Adapters on 10.1.0 or later (known from the manifest's `sdkVersion`)
  receive them on standard input instead;
- **.NET resident adapters on SDK 10.0.x**;
- **versions published by older tools** under `adapters/{id}/{version}`, when asked for
  `{id}/{version}`;
- **packages carrying `source/`**, which it does not unpack, and packages from before `source/`
  existed that happen to have a folder named `source`, which it unpacks as before (only a folder the
  manifest declares as source is skipped).

## Older hosts and current packages

Hosts on `SimplyWorks.Serverless` 10.0.x, and applications that list adapters by reading the keys
under `adapters/`, do not know manifests, versions or the catalog. With them:

- `adapters/{id}` always holds the current .NET package, with the metadata they need
  (`EntryAssembly`, `Hash`), after every publish, promote or rollback. They run it.
- Nothing else is ever written under `adapters/`, so their listings see exactly the adapters that
  exist.
- A package's `adapter.json` is just another file to them, and `source/` just another folder.
- A .NET adapter built on the current SDK still reads settings from command-line arguments when an
  older host passes them that way.
- Python, Node and `exec` adapters are never written to `adapters/{id}`, so older hosts never see
  them, rather than try to start them with `dotnet`. For the same reason such adapters must be
  published with a version, and an id once published as a .NET adapter cannot move to another
  runtime.
- They cannot pin a version published in the current layout: asked for `{id}/{version}`, they look
  only at `adapters/{id}/{version}`. Current hosts look in both places.

## The protocol

Protocol 2 is designed to grow without breaking deployed adapters or hosts:

- **Version negotiation.** The host offers its newest version in the handshake. The adapter answers
  in Hello with the lower of that and its own newest. The host accepts any version in its range
  (`ProtocolVersions.Min` to `Max`, both 2 today). A new protocol version raises the host's `Max`
  and keeps `Min`, so adapters already deployed keep working. The SDKs refuse only a host older
  than protocol 2.
- **New fields are optional.** Fields added to the messages (Hello's `sdk_language`, `settings`,
  `kinds` and `contracts`; `CommandInfo`'s schemas; Invoke's `properties`) are ignored by older
  hosts, and left empty by older adapters, which hosts handle.
- **New behaviour is announced.** The host sends `Cancel` only to adapters that list the `cancel`
  capability in Hello, so an adapter built before it never receives a frame it does not know.

Protocol 1 is frozen. The CLI and the host treat its markers (`#!#`, `{{null}}`, `{{newline}}`,
`{{error}}`, `{{expected}}`, `{{quit}}`) as fixed by every adapter already published.

## Manifests, catalog entries and descriptions

- Every field is optional when read. A missing manifest, or one from an older tool, means what it
  always meant.
- Fields a reader does not know are kept and written back unchanged, at every level, in manifests,
  catalog entries and `--describe` output. A manifest written by a newer tool survives a round trip
  through an older one.
- `manifestVersion` is never lowered by a tool that rewrites a manifest.
- `compatibility.MinVersionOf(application)` also reads the older `min<Application>Version` form.

## Storage

The layout is held to three rules (see [Packaging and storage](packaging-and-storage.md)):

1. `adapters/{id}` always holds the current .NET package with the metadata an older host needs.
2. Nothing but `adapters/{id}` is written under `adapters/`. Versions older tools put at
   `adapters/{id}/{version}` are read, never written.
3. Everything new lives beside it: `adapters-versions/` and `adapters-catalog/`.

An adapter published only by older tools has no catalog entry. `versions` then reads its packages
instead, and its next `publish` or `promote` creates the entry from them.

## The CLI

- The original form, `sw-serverless [options] <project.csproj> <adapter-id>`, works as it always
  has. Without `-v` it makes the same unversioned upload to `adapters/{id}`.
- Command words (`init`, `build`, `publish`, ...) are recognised only as the first argument, and
  only when no file of that name exists, so a project file that happens to be named like a command
  is still published.

## Requiring a newer host or application

When an adapter relies on something a host gained in a given release, say so in its
`adapter.json`:

```json
{ "compatibility": { "minHostVersion": "10.1.0" } }
```

An older host refuses to install it, with a message naming both versions, instead of failing in a
less obvious way. Hosts before `minHostVersion` existed do not check it.

`compatibility.applications` does the same for an application's own versions. Hosts ignore it;
the application checks its own entry with `MinVersionOf`. See
[The manifest](manifest.md#compatibility).

## How this is tested

`SW.Serverless.CompatibilityTests` runs these promises against released packages:

- a host built on the released `SimplyWorks.Serverless` 10.0.0 installs and runs what the current
  CLI publishes, follows promote and rollback, runs unversioned publishes, and still runs versions
  in the old layout;
- the current host runs packages without manifests, versions in the old layout, and adapters built
  on the released `SimplyWorks.Serverless.Sdk` 10.0.0, classic and resident;
- the old listing rule sees exactly the adapters that exist after versions, promote and withdraw;
- manifests and catalog entries with unknown fields round-trip through the current and the released
  10.0.2 parser;
- Python, Node and `exec` packages stay out of older hosts' sight, must be versioned, and cannot
  take over a .NET adapter's id.
