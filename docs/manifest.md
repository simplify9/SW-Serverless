# The manifest: adapter.json

Every package has an `adapter.json` at its root: the **manifest**. It tells a host how to run the
adapter, and tells an application what the adapter needs and how to present it.

There are two files with that name:

- **The one you write**, beside your project. It holds what only you know: the id, the version,
  names and descriptions, the icon, and for Python and Node the runtime and entry file. It can be
  short.
- **The one in the package**, which `sw-serverless build` writes. It starts from yours and adds what
  the build learns from the built adapter: entry, lifecycle, protocol, SDK version, settings, kinds,
  contracts, source files. Where yours and the adapter's own description disagree, the adapter
  wins.

You never edit the second one by hand.

## A complete example

What you write, for a Python adapter:

```json
{
  "id": "acme.orders",
  "version": "1.2.0",
  "displayName": "Acme Orders",
  "summary": "Sends orders to Acme and returns their receipts.",
  "description": "Sends each order to Acme's order API.\n\nNeeds an **API key** from Acme.",
  "publisher": { "name": "Acme Integrations", "url": "https://acme.example", "email": "dev@acme.example" },
  "license": "MIT",
  "homepage": "https://acme.example/adapters/orders",
  "repository": "https://github.com/acme/orders-adapter",
  "icon": "icon.png",
  "tags": ["orders", "partners"],
  "categories": ["Partners"],
  "runtime": "python",
  "entry": "main.py",
  "compatibility": {
    "minHostVersion": "10.1.0",
    "applications": { "orders-app": "3.2.0" }
  },
  "properties": [
    { "name": "Endpoint", "displayName": "API URL", "group": "Connection" },
    { "name": "ApiKey", "displayName": "API key", "group": "Connection" },
    { "name": "Mode", "type": "select", "options": ["test", "live"] }
  ]
}
```

What the build writes into the package (abridged), given an adapter that declares the settings
`Endpoint`, `ApiKey` (secret) and `Mode` (default `test`) and implements the `orders` contract's
`processor` kind:

```json
{
  "manifestVersion": 1,
  "id": "acme.orders",
  "version": "1.2.0",
  "displayName": "Acme Orders",
  "summary": "Sends orders to Acme and returns their receipts.",
  "kinds": ["processor"],
  "runtime": "python",
  "language": "python",
  "entry": "_serverless_entry.py",
  "runtimeVersion": ">=3.12",
  "contracts": { "orders": 1 },
  "source": {
    "path": "source",
    "files": { "adapter.json": "9c1f…", "icon.png": "51d0…", "main.py": "a775…", "requirements.txt": "a43a…" },
    "buildCommand": "sw-serverless build",
    "lockfiles": ["requirements.txt"]
  },
  "lifecycle": "classic",
  "protocol": { "min": 2, "max": 2 },
  "sdkVersion": "10.2.0",
  "compatibility": { "minHostVersion": "10.1.0", "applications": { "orders-app": "3.2.0" } },
  "properties": [
    { "name": "Endpoint", "displayName": "API URL", "description": "Where orders go.", "type": "text", "required": true, "secret": false, "group": "Connection" },
    { "name": "ApiKey", "displayName": "API key", "type": "text", "required": true, "secret": true, "group": "Connection" },
    { "name": "Mode", "type": "select", "required": false, "secret": false, "default": "test", "options": ["test", "live"] }
  ]
}
```

`sw-serverless publish` then sets `version` (if you passed `-v`) and `publishedOn`.

## Fields

"You" means the field comes from the `adapter.json` you write. "Build" means `sw-serverless build`
sets it, whatever you wrote.

### Identity

| Field | Set by | Meaning |
|---|---|---|
| `manifestVersion` | build | The manifest format version: `1`. |
| `id` | you (required) | The adapter id: lowercase letters, digits, `.`, `_` and `-`, starting with a letter or digit. |
| `version` | you, or `publish -v` | A semantic version, such as `1.4.0` or `1.5.0-rc.1`. `publish` uses it unless `-v` says otherwise. |
| `publishedOn` | publish | When it was published. |

### Presentation

All optional, all yours. Applications use them to list and describe the adapter.

| Field | Meaning |
|---|---|
| `displayName` | Its name, for people. |
| `summary` | One line, for a list or a card. |
| `description` | A longer description, in Markdown. |
| `publisher` | `{ "name", "url", "email" }`. |
| `license`, `homepage`, `repository` | Strings. |
| `icon` | A PNG, JPEG or SVG file inside the package, as a path from the package root. Icons of 64 KB or less are also copied into the catalog. For .NET, make sure the file reaches the publish output (for example with `CopyToPublishDirectory`); `publish` fails if it is missing. |
| `tags`, `categories` | Lists of strings. |
| `releaseNotes` | What changed in this version, in Markdown. `publish --notes` replaces it. |

### Running

| Field | Set by | Meaning |
|---|---|---|
| `runtime` | you (Python, Node, `exec`); build | `dotnet` (the default), `python`, `node` or `exec`. |
| `entry` | you (Python, Node, `exec`); build | The file the runtime starts, as a path from the package root. For Python you name your script (default `main.py`) and the build replaces it with `_serverless_entry.py`, a small script it writes that sets up the vendored packages and runs yours. For Node you name your script (default `main.js`; a `.ts` entry becomes `.js`). For .NET the build finds the entry assembly. |
| `entries` | you | A different entry per platform, for a package that carries one build per platform: `{ "linux-x64": "bin/linux-x64/adapter", "osx-arm64": "bin/osx-arm64/adapter" }`. Every platform named must also be in `platforms`. `entry` stays the default. |
| `runtimeVersion` | you; build sets a default | The runtime versions it needs. Comparisons separated by commas or spaces: `>=3.12`, `>=22, <24`. A bare version matches that line: `3.12` is any `3.12.x`. Python defaults to `>=3.12`, Node to `>=22`. For .NET it is checked only when you set it. |
| `platforms` | you; build | The platforms it runs on: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`. Absent means anywhere its runtime runs. The build sets it when a Python requirement or a Node dependency has native code. A host on another platform refuses the package. |
| `lifecycle` | build | `classic` or `resident`, from what the adapter itself reports. For a .NET adapter that calls `Runner.RunResident`, write `"lifecycle": "classic"` to have it run as classic sessions on protocol 2; see [Writing adapters](writing-adapters.md#a-net-adapter-on-protocol-2-run-as-a-classic-session). |
| `protocol` | build | `{ "min": 2, "max": 2 }` for an adapter on protocol 2. Absent for a .NET adapter on protocol 1. |
| `language` | you; build sets a default | For listing: `csharp`, `fsharp`, `vb`, `python`, `javascript`, `typescript`. |
| `sdkVersion` | build | The SDK version the adapter was built with. Hosts use it to decide whether a .NET classic adapter reads its settings from standard input. |

### What it is for

| Field | Set by | Meaning |
|---|---|---|
| `kinds` | you and the adapter | The kinds it implements. The build combines yours with those the adapter declares in code. |
| `contracts` | you and the adapter | The contracts it implements, with versions: `{ "orders": 1 }`. Combined the same way; where both name a contract, the adapter's version wins. |

### Properties

`properties` lists the adapter's settings, with what a form needs to ask for them.

| Property field | Meaning |
|---|---|
| `name` | The setting's name. Letters, digits, `_`, `.`, `:` and `-`, starting with a letter or `_`. |
| `displayName` | A label. |
| `description` | Help text. |
| `type` | `text` (the default), `multiline`, `number`, `boolean`, `select` or `json`. |
| `required` | Whether it must be given. |
| `secret` | Masked wherever it is shown. A secret's default is never written into the manifest. |
| `default` | The value used when none is given. |
| `options` | The choices of a `select`. |
| `group` | A heading to group related settings under, such as `Connection`. |

The settings themselves come from the adapter's code. The build writes one property per setting
the adapter declares: `name`, `required`, `secret`, `default` and `description` come from the code.
From a property of the same name in your file it keeps `displayName`, `group` and `options`, and
`type` unless your type is `text`. A property in your file that the code does not declare is
dropped. The conformance kit fails a package whose properties and declared settings disagree.

### Source

| Field | Set by | Meaning |
|---|---|---|
| `source.path` | build | The folder in the package that holds the source: `source`. |
| `source.files` | build | Every source file, as a path within that folder, with its SHA-256 in hex. |
| `source.buildCommand` | build | How the package was built: `dotnet publish -c Release` or `sw-serverless build`. |
| `source.lockfiles` | build | The dependency lockfiles among the source: `packages.lock.json`, `requirements.txt`, `poetry.lock`, `package-lock.json`, `yarn.lock` and others. |

Absent when built with `--no-source`. See [Packaging and storage](packaging-and-storage.md#source).

### Compatibility

| Field | Meaning |
|---|---|
| `compatibility.minHostVersion` | The oldest SW-Serverless host that may run the adapter. A host older than this refuses to install it. Set it when the adapter relies on host behaviour added in a given release. A host's own version is `SW.Serverless.HostInfo.Version`. |
| `compatibility.applications` | The oldest version of each application that may use the adapter, by application name: `{ "orders-app": "3.2.0" }`. Hosts ignore it. An application reads its own entry and decides. |

An application reads its minimum with `MinVersionOf`:

```csharp
var minimum = manifest.Compatibility?.MinVersionOf("orders-app");
if (minimum != null && Version.Parse(minimum) > myVersion)
    throw new InvalidOperationException($"This adapter needs orders-app {minimum} or later.");
```

`MinVersionOf(name)` also reads the older form `"min<name>Version"` written directly under
`compatibility`, for manifests from before `applications` existed: for an application named
`Orders`, `"minOrdersVersion": "3.2.0"`. It matches names without regard to case.

### Fields the model does not know

A manifest may contain fields this version does not know, written by a newer tool. They are kept
and written back unchanged by every tool that reads and rewrites the manifest.

## Validation

`sw-serverless manifest validate`, `build`, `publish` and the conformance kit all check:

- `manifestVersion` is 1 or more;
- `id`, if present, uses only lowercase letters, digits, `.`, `_` and `-`, starting with a letter or
  digit;
- `version`, if present, is a semantic version (`1.4.0`, `1.4.0-beta.1`);
- `entry`, `icon`, every `entries` value and `source.path` are paths inside the package: relative,
  no leading `/` or `\`, no `:`, no `..`;
- `runtime` is a lowercase name (letters, digits, `.`, `-`);
- every platform looks like `linux-x64`;
- every platform named in `entries` is in `platforms`;
- every contract has a name and a version of 1 or more;
- every source file has a 64-digit hex SHA-256;
- `lifecycle` is `classic` or `resident`;
- `protocol.min` is not above `protocol.max`;
- `compatibility.minHostVersion` and every `compatibility.applications` value are versions;
- every property has a valid name, no name appears twice, every `type` is known, and every
  `select` has `options`.

`build` also requires an `id`, and `publish` requires a version, from the manifest or `-v`.

## The original publishing form

When a .NET project is published directly with `sw-serverless <project.csproj> <id>`, the
manifest is made from the `adapter.json` beside the project file, if any, and the build output:

- `id`, `version`, `runtime` (`dotnet`), `entry`, `lifecycle`, `protocol`, `sdkVersion` and
  `publishedOn` are set by the tool;
- `kinds` come from `-k` if given, else from your file, else from `[AdapterKind]` attributes;
- `properties` come from your file if it lists any; otherwise the tool starts a classic adapter and
  asks it for its declared settings (skip with `--no-probe`);
- everything else is yours, as written.
