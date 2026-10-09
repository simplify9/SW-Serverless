# Packaging and storage

This page describes what is inside a package, how `sw-serverless build` makes one for each
language, and where packages, versions and the catalog are kept in storage.

- [What a package contains](#what-a-package-contains)
- [.NET](#net) · [Python](#python) · [Node and TypeScript](#node-and-typescript) · [exec](#exec)
- [Source](#source)
- [Storage layout](#storage-layout)
- [The catalog](#the-catalog)
- [How a host finds a package](#how-a-host-finds-a-package)
- [Older hosts](#older-hosts)

## What a package contains

A package is a zip. At its root:

- the adapter's runnable files;
- its dependencies, so that installing it fetches nothing;
- `adapter.json`, the full [manifest](manifest.md);
- `source/`, the source it was built from, unless built with `--no-source`.

`sw-serverless build` writes the unpacked package to `bin/serverless/package/` and the zip to
`bin/serverless/{id}-{version}.zip` in the project (or under `--out`).

## .NET

The build runs `dotnet publish -c Release` and packages its output. The adapter must reference
`SimplyWorks.Serverless.Sdk` 10.1.0 or later, because the build asks the built adapter to describe
itself with `--describe`. Dependencies come from NuGet as usual and are part of the publish output.

```
acme.orders-1.2.0.zip
├── AcmeOrders.dll            entry
├── AcmeOrders.deps.json
├── AcmeOrders.runtimeconfig.json
├── SW.Serverless.Sdk.dll     and the other dependencies
├── adapter.json
└── source/
    ├── AcmeOrders.csproj
    ├── Program.cs
    ├── packages.lock.json
    └── adapter.json
```

Projects the adapter references with `<ProjectReference>` are carried in `source/` too, so the
source rebuilds. The build warns when one lies outside the repository.

## Python

Python runs from source, so the package is the adapter's own files, as the [source rules](#source)
select them, plus:

- `_vendor/sw_serverless/`: the SDK, copied from the copy the CLI carries. No PyPI access is
  needed for it.
- `_vendor/...`: what `requirements.txt` names, installed by `pip` from wheels. A line naming the
  SDK (`sw-serverless`) is skipped.
- `_serverless_entry.py`: a small script the build writes. It puts `_vendor` (and the folder for
  this platform, if there is one) on the import path and runs your entry script. It becomes the
  manifest's `entry`.

```
acme.orders-1.2.0.zip
├── _serverless_entry.py      entry
├── main.py
├── requirements.txt
├── _vendor/
│   ├── sw_serverless/
│   └── requests/ …           pure-Python requirements
├── adapter.json
└── source/
```

**Native code.** The build downloads wheels only (`--only-binary=:all:`), for CPython 3.12, and
never compiles anything. If every requirement is pure Python, it is installed once into `_vendor/`
and the package runs anywhere. If any requirement has native code, every requirement is installed
once per target platform, into `_vendor/<platform>/`, and the manifest's `platforms` lists those
platforms, so a host on any other refuses the package instead of failing on import. The targets
are `linux-x64` and `linux-arm64` unless `adapter.json` names others in `platforms`. Supported
targets are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` and `win-x64`. A requirement with no
wheel for a target fails the build.

The build machine needs `python3` with `pip`, and network access to your package index when
`requirements.txt` names anything besides the SDK.

## Node and TypeScript

Node runs JavaScript from source, so the package is the adapter's own files, plus:

- `node_modules/`, from `npm ci` (when `package-lock.json` exists) or `npm install`, with
  `--omit=dev` and `--ignore-scripts`. Install scripts of dependencies are not run.
  `devDependencies` are left out. A `@simplyworks/sw-serverless` entry is removed before `npm` runs.
- `node_modules/@simplyworks/sw-serverless/`: the SDK, copied from the copy the CLI carries.

**TypeScript.** Each `.ts`, `.mts` or `.cts` file (except `.d.ts`) is turned into `.js`, `.mjs` or
`.cjs` by Node's own type stripping, and the TypeScript file is removed from the package (it stays
in `source/`). Nothing is compiled: syntax that cannot simply be erased, such as `enum`,
`namespace` or constructor parameter properties, fails the build with its file and line. This needs
Node 22.13 or later on the build machine. An entry of `main.ts` becomes `main.js` in the manifest.

**Native addons.** A dependency with a native addon (`*.node`) is built by `npm` for the machine
it runs on, and cannot be built for another platform from there. The build refuses it unless
`adapter.json` has `"platforms"` set to exactly the platform you are building on; build on the
platform the adapter will run on.

```
acme.orders-1.2.0.zip
├── main.js                   entry (from main.ts)
├── package.json
├── node_modules/
│   ├── @simplyworks/sw-serverless/
│   └── …
├── adapter.json
└── source/
```

## exec

An `exec` adapter is a self-contained executable that speaks [protocol 2](protocol.md): a Go or
Rust program, a native-compiled .NET program, or anything else. The host starts the entry file
itself. `sw-serverless build` does not build `exec` adapters; you make the zip yourself, with the
binary and an `adapter.json`:

```json
{
  "id": "acme.fast-parser",
  "version": "1.0.0",
  "runtime": "exec",
  "lifecycle": "resident",
  "protocol": { "min": 2, "max": 2 },
  "entry": "fast-parser",
  "platforms": ["linux-x64"]
}
```

To carry one binary per platform, list them in `entries`:

```json
{
  "id": "acme.fast-parser",
  "version": "1.0.0",
  "runtime": "exec",
  "lifecycle": "classic",
  "protocol": { "min": 2, "max": 2 },
  "entry": "linux-x64/fast-parser",
  "platforms": ["linux-x64", "linux-arm64", "osx-arm64"],
  "entries": {
    "linux-x64": "linux-x64/fast-parser",
    "linux-arm64": "linux-arm64/fast-parser",
    "osx-arm64": "osx-arm64/fast-parser"
  }
}
```

Then check and publish it like any other package:

```sh
(cd package && zip -r ../acme.fast-parser-1.0.0.zip .)
sw-serverless test acme.fast-parser-1.0.0.zip
sw-serverless publish acme.fast-parser-1.0.0.zip -p s3 -b my-adapters
```

The host sets the execute bit on the entry when it installs the package on Linux and macOS. For
`sw-serverless test` to pass, the binary must also answer `--describe`
([format](protocol.md#describe)).

## Source

Unless you pass `--no-source`, the build copies the project's source into the package under
`source/` and records each file's SHA-256 in the manifest's `source.files`. Anyone holding a
published version can then read it, compare two versions, or rebuild it. Hosts never unpack
`source/`.

Which files are carried:

1. Everything in the project folder (and, for .NET, the folders of referenced projects),
2. minus a built-in list that is always left out:
   - build output and dependencies: `bin/`, `obj/`, `dist/`, `build/`, `out/`, `target/`,
     `__pycache__/`, `*.pyc`, `.venv/`, `venv/`, `node_modules/`, `.pytest_cache/`,
     `.mypy_cache/`, `packages/`, `*.nupkg`;
   - version control and editors: `.git/`, `.vs/`, `.idea/`, `.vscode/`, `*.user`, `*.suo`,
     `.DS_Store`;
   - files that usually hold secrets: `.env`, `.env.*`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.jks`,
     `id_rsa*`, `id_ed25519*`, `id_ecdsa*`, `secrets.json`, `*.publishsettings`, `*.pubxml`,
     `*.pubxml.user`, `appsettings.*.json`;
3. minus what the project's `.gitignore` and `.serverlessignore` exclude, with gitignore's rules
   (`*`, `**`, a trailing `/` for folders, a leading `/` to anchor, `!` to bring a file back).

For Python and Node, the same rules decide which of your files go into the package to run.

Before building, every text file carried is scanned for things that look like secrets: private
keys, cloud and service tokens (AWS, GitHub, Slack, Google, Stripe, Azure storage keys), passwords
in connection strings, JSON web tokens, and secrets assigned in code. Obvious placeholders such as
`changeme` or `${API_KEY}` are ignored. A finding fails the build and names the file and line. If it
is a false positive, pass `--allow <file>`. Use `--dry-run` to see what would be carried.

The build warns when the source is larger than 10 MB, which usually means build output slipped in.

## Storage layout

Everything is under one root folder in the bucket, `adapters` unless a deployment uses another
(`ServerlessOptions.AdapterRemotePath` on the host).

| Key | What | Written by |
|---|---|---|
| `adapters/{id}` | The current package of a .NET adapter. What hosts from before versions run. | `publish` and `promote` of a .NET adapter (unless `--no-promote`); the original publishing form |
| `adapters-versions/{id}/{version}` | One package per version. Never overwritten. | `publish`; the original form with `-v` |
| `adapters-catalog/{id}.json` | The catalog entry. | Every command except `versions` |
| `adapters/{id}/{version}` | Where publishing tools from before this layout put versions. | Read only, never written |

Ids and versions in keys are lowercase.

Each package object carries storage metadata, which older hosts read:

| Metadata | Value |
|---|---|
| `EntryAssembly` | The entry file. |
| `Hash`, `Sha256` | The hex SHA-256 of the zip. Hosts name the unpacked folder after `Hash`. |
| `Lifecycle` | `classic` or `resident`. |
| `Kind` | The kinds, comma separated. |
| `Version` | The version, or empty for an unversioned upload. |
| `Timestamp` | When it was uploaded, UTC. |
| `Lang` | `dotnet`. |

Versions and the catalog are kept beside `adapters/`, not inside it, for two reasons: older hosts
and applications treat every key under `adapters/` as an adapter, and storage backed by a file
system cannot hold `adapters/{id}` as a file and a folder at once.

## The catalog

`adapters-catalog/{id}.json` describes everything published of one adapter, so an application can
list adapters, their versions and their settings without opening a package:

```json
{
  "catalogVersion": 1,
  "id": "greeter",
  "current": "1.1.0",
  "manifest": { "…": "the current package's manifest" },
  "sha256": "421ff604eef7abe8c288ab3f04e2914a801df2e0a590953946a89217a86ce5e2",
  "iconDataUri": "data:image/png;base64,…",
  "updatedOn": "2026-10-09T15:24:57.854628+00:00",
  "versions": [
    {
      "version": "1.0.0",
      "sha256": "0b7d14e6c2a9…",
      "publishedOn": "2026-09-01T10:00:00+00:00",
      "publishedBy": "ada",
      "manifest": { "…": "that version's manifest" },
      "withdrawn": false
    },
    {
      "version": "1.1.0",
      "sha256": "421ff604eef7…",
      "publishedOn": "2026-10-09T15:24:57.815776+00:00",
      "publishedBy": "ci-bot",
      "manifest": { "…": "…" },
      "withdrawn": false
    }
  ]
}
```

- `current` is null after an unversioned upload (the original form without `-v`): an unversioned
  package is running.
- `versions` is oldest first. A record never changes once written, except `withdrawn`.
- `iconDataUri` is set when the icon is a PNG, JPEG or SVG of 64 KB or less.

Read it from .NET with `AdapterCatalogStore` (in `SimplyWorks.Serverless.Contract`):

```csharp
using SW.Serverless.Contract.Catalog;

var catalog = new AdapterCatalogStore(cloudFiles);            // root "adapters"
foreach (var entry in await catalog.ListAsync())
    Console.WriteLine($"{entry.Id} {entry.Current}: {entry.Manifest?.DisplayName}");

var one = await catalog.GetAsync("greeter");                  // null if it has no entry
var pinnable = one.Versions.Where(v => !v.Withdrawn).Select(v => v.Version);
```

An adapter published only by tools from before the catalog has no entry until its next publish or
promote, which create one from the packages and their metadata.

`AdapterCatalogPaths` builds the keys: `Current(root, id)`, `Version(root, id, version)`,
`Catalog(root, id)`, and `Ref(id, version)` / `Split(ref)` for pinned ids.

## How a host finds a package

| Asked for | The host reads |
|---|---|
| `greeter` | `adapters/greeter`; if there is none (Python, Node, `exec`), the catalog's `current` version at `adapters-versions/greeter/{current}` |
| `greeter/1.4.0` | `adapters-versions/greeter/1.4.0`; if there is none, `adapters/greeter/1.4.0` |

It then unpacks the package (without `source/`) and checks its manifest, as described in
[Hosting adapters](hosting.md#how-installation-works).

## Older hosts

Hosts on `SimplyWorks.Serverless` 10.0.x, and applications that list adapters by reading
`adapters/`, predate manifests, versions and the catalog. What is published stays usable by them:

- `adapters/{id}` always holds the current .NET package, with the metadata above, after every
  publish, promote or rollback.
- Nothing is written under `adapters/` except `adapters/{id}`.
- A Python, Node or `exec` package is never written to `adapters/{id}`, because an older host would
  start it with `dotnet`. Such adapters are invisible to older hosts. This is also why they must be
  published with a version, and why an id once used for a .NET adapter cannot switch runtime.
- An older host asked for `{id}/{version}` looks only at `adapters/{id}/{version}`, so it cannot
  pin versions published in the current layout.

More in [Compatibility](compatibility.md).
