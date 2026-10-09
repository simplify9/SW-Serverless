# The CLI: sw-serverless

`sw-serverless` creates, builds, tests, runs and publishes adapters in any supported language, and
manages published versions.

- [Installing](#installing)
- [Commands at a glance](#commands-at-a-glance)
- [init](#init) · [build](#build) · [test](#test) · [run](#run) · [manifest validate](#manifest-validate)
- [publish](#publish) · [promote](#promote) · [versions](#versions) · [withdraw](#withdraw)
- [Publishing a .NET project directly (the original form)](#publishing-a-net-project-directly-the-original-form)
- [Storage](#storage)
- [How versions are chosen](#how-versions-are-chosen)
- [Exit codes](#exit-codes)

## Installing

Self-contained binaries are published on the
[GitHub releases](https://github.com/simplify9/SW-Serverless/releases) tagged `cli-v<version>`:

| Platform | Asset |
|---|---|
| Linux x64 | `sw-serverless-linux-x64.tar.gz` |
| Linux Arm64 | `sw-serverless-linux-arm64.tar.gz` |
| Linux x64, musl (Alpine) | `sw-serverless-linux-musl-x64.tar.gz` |
| macOS Intel | `sw-serverless-osx-x64.tar.gz` |
| macOS Apple silicon | `sw-serverless-osx-arm64.tar.gz` |
| Windows x64 | `sw-serverless-win-x64.zip` |

Each release also has a `SHA256SUMS` file. On Linux and macOS the install script finds the newest
release, picks your platform's archive, checks it against `SHA256SUMS` and installs `sw-serverless` to
`~/.local/bin` (or `INSTALL_DIR`); `SW_SERVERLESS_VERSION` picks a version:

```sh
curl -fsSL https://raw.githubusercontent.com/simplify9/SW-Serverless/main/scripts/install-cli.sh | sh
SW_SERVERLESS_VERSION=10.2.0 INSTALL_DIR=/usr/local/bin sh install-cli.sh
```

By hand, or on Windows: download your platform's archive and `SHA256SUMS` from the release, check
one against the other (`sha256sum -c` on Linux, `shasum -a 256 -c` on macOS,
`Get-FileHash` on Windows), unpack it, and put `sw-serverless` (`sw-serverless.exe`) on your `PATH`.

To build it from source you need the .NET 10 SDK:

```sh
git clone https://github.com/simplify9/SW-Serverless.git
cd SW-Serverless
dotnet run --project SW.Serverless.Installer -- build ../my-adapter      # run any command without installing
dotnet publish SW.Serverless.Installer -c Release -o ./out               # ./out/sw-serverless
```

What each command needs on the machine:

| Command | Needs |
|---|---|
| `build`, `test`, `run` of a .NET adapter | The .NET 10 SDK (`dotnet`) |
| `build`, `test`, `run` of a Python adapter | `python3` 3.12 or later; `pip` if it has a `requirements.txt` |
| `build`, `test`, `run` of a JavaScript adapter | `node` 22 or later; `npm` if its `package.json` has dependencies |
| `build` of a TypeScript adapter | `node` 22.13 or later |
| `publish`, `promote`, `versions`, `withdraw` | Access to the storage |

`sw-serverless <command> --help` lists a command's options.

## Commands at a glance

| Command | What it does |
|---|---|
| `sw-serverless init <Name>` | Writes a new adapter project. |
| `sw-serverless build [project]` | Builds a package: `bin/serverless/{id}-{version}.zip`. |
| `sw-serverless test [package]` | Runs the adapter as a host would and checks it: manifest, description, settings, contracts. |
| `sw-serverless run [package] --call <Command>` | Starts the adapter, calls one command, prints the result. |
| `sw-serverless manifest validate [path]` | Checks an `adapter.json`. |
| `sw-serverless publish <package.zip>` | Publishes a built package as a version. |
| `sw-serverless promote <id> <version>` | Makes a published version the current one. |
| `sw-serverless versions <id>` | Lists published versions. |
| `sw-serverless withdraw <id> <version>` | Marks a version as not to be used. |
| `sw-serverless <project.csproj> <id>` | Builds and publishes a .NET project in one step (the original form). |

**Options that take several values** (`--allow`, `--contract`) take them after one flag, separated
by spaces: `--contract a.json b.json`. Giving the flag twice is an error. Because such an option
takes every value that follows it, put positional arguments before it.

## init

```sh
sw-serverless init <Name> [--lang dotnet|python|node|typescript] [--id <id>] [--dir <folder>]
```

Writes a new adapter in `<folder>/<Name>`: a greeter with two settings (`Greeting`, and a secret
`ApiKey`) and two commands (`Greet`, `Count`), an `adapter.json`, a `settings.example.json`, a
`.gitignore` and a `README.md`.

| Option | Default | Meaning |
|---|---|---|
| `Name` | (required) | The project, folder and class name. Letters, digits and `_`, starting with a letter. |
| `--lang` | `dotnet` | `dotnet`, `python`, `node` (JavaScript) or `typescript`. |
| `--id` | from the name | The adapter id. `AcmeOrders` becomes `acme.orders`. |
| `--dir` | `.` | The folder to create the project in. The project folder must not exist or must be empty. |

Files per language:

| Language | Files |
|---|---|
| `dotnet` | `<Name>.csproj` (referencing `SimplyWorks.Serverless.Sdk` 10.1.0, with a NuGet lock file), `Program.cs` |
| `python` | `main.py`, `requirements.txt` |
| `node` | `main.js`, `package.json` |
| `typescript` | `main.ts`, `package.json` (`"type": "module"`), `tsconfig.json` |

```sh
sw-serverless init AcmeOrders --lang python
# Made an adapter in /work/AcmeOrders:
#   adapter.json
#   main.py
#   ...
```

## build

```sh
sw-serverless build [project] [-o <folder>] [--no-source] [--dry-run] [--allow <file> ...]
```

Builds the adapter in `project` (default: the current folder), which must hold an `adapter.json`
with an `id`. The runtime in `adapter.json` decides how:

- **.NET** (no `runtime`, or `"dotnet"`): `dotnet publish -c Release` of the one `.csproj`,
  `.fsproj` or `.vbproj` in the folder. The adapter must use `SimplyWorks.Serverless.Sdk` 10.1.0 or
  later. An adapter on an older SDK is published with the
  [original form](#publishing-a-net-project-directly-the-original-form).
- **Python** (`"python"`): copies the adapter's files, copies the SDK under `_vendor/`, and installs
  `requirements.txt` there with `pip`.
- **Node** (`"node"`): copies the adapter's files, turns TypeScript into JavaScript, installs
  `package.json` dependencies with `npm`, and copies the SDK into `node_modules/`.

Then it runs the adapter with `--describe`, writes the full `adapter.json` into the package, adds
the source under `source/`, and zips it. Details: [Packaging and storage](packaging-and-storage.md).

| Option | Default | Meaning |
|---|---|---|
| `project` | `.` | The adapter's project folder. |
| `-o`, `--out` | `<project>/bin/serverless` | Where the package folder (`package/`) and the zip go. |
| `--no-source` | off | Leave the source out of the package. |
| `--dry-run` | off | List the source files that would be carried, check them for secrets, and build nothing. |
| `--allow` | none | Source files (relative to the project) whose secret-scan finding is a false positive. |

The zip is named `{id}-{version}.zip`, or `{id}.zip` when `adapter.json` has no version.

The build prints the source files it carried and their sizes, and any warnings. It fails, before
building anything, if a source file looks like it holds a secret:

```
src/Client.cs:14 looks like a password in a connection string. Remove it, or pass --allow src/Client.cs if it isn't one
```

## test

```sh
sw-serverless test [package] [--settings <file>] [--timeout <seconds>] [--allow-delete] [--contract <file> ...]
```

Runs the conformance kit: starts the adapter the way a host does (from a temporary local store,
through the real host library) and checks it. `package` is a package zip, an unpacked package
folder, or a project folder, which is built first into a temporary folder. Default: the current
folder.

| Option | Default | Meaning |
|---|---|---|
| `--settings` | none | A JSON file of settings: `{ "Name": "value" }`. The adapter really runs with them, so it calls whatever they point at. |
| `--timeout` | `60` | Seconds one call may take. |
| `--allow-delete` | off | Let methods a contract marks destructive run. Off by default because they change the real source the settings point at. |
| `--contract` | none | Contract files to check against. The CLI carries no contracts of its own. See [Contracts](contracts.md). |

It prints one line per check, `PASS`, `FAIL` or `SKIP`, then `Conforms.` or the number of failed
checks:

```
PASS manifest
PASS describe — python SDK 10.2.0, 2 commands
PASS settings match the manifest — 2 settings
PASS starts — classic session
SKIP contracts — it declares no contract, so only what every adapter must do is checked
PASS an unknown command is refused — with an error, and it kept answering
Conforms.
```

What each check means is in [Contracts](contracts.md#what-the-conformance-kit-checks). The exit code
is 0 only if no check failed.

## run

```sh
sw-serverless run [package] --call <Command> [--input <value>] [--settings <file>] [--timeout <seconds>]
```

Starts the adapter as a host would, calls one command, prints its result, and stops it. `package`
is taken as for `test`.

| Option | Default | Meaning |
|---|---|---|
| `--call` | (required) | The command name. |
| `--input` | none | The argument. Valid JSON is sent as JSON; anything else as text. `@file` reads it from a file. |
| `--settings` | none | A JSON file of settings. |
| `--timeout` | `60` | Seconds the call may take. |

```sh
sw-serverless run --settings settings.json --call Greet --input Ada
# Hello, Ada!
sw-serverless run bin/serverless/acme.orders-1.2.0.zip --call Process --input @order.json
```

A string result is printed as it is; any other result is printed as JSON. If the call fails, the
error message is printed and the exit code is 1.

## manifest validate

```sh
sw-serverless manifest validate [path]
```

Checks an `adapter.json`, or the one in the folder `path` (default: the current folder), against
the [validation rules](manifest.md#validation). Prints each problem, then `<path> is valid.` or the
number of problems.

## publish

```sh
sw-serverless publish <package.zip> [-v <version>] [--no-promote] [--notes <text>] [--published-by <name>] [storage options]
```

Publishes a package made by `sw-serverless build`, in any language, as a version.

| Option | Default | Meaning |
|---|---|---|
| `package` | (required) | The zip. |
| `-v`, `--version` | the manifest's `version` | An explicit version (`1.4.0`, `1.5.0-rc.1`) or `major`, `minor`, `patch` to bump the highest published release. See [How versions are chosen](#how-versions-are-chosen). |
| `--no-promote` | off | Publish the version without making it current. |
| `--notes` | none | Release notes (Markdown). Replaces `releaseNotes` in the manifest. |
| `--published-by` | `SWSL_PUBLISHED_BY`, then `GITHUB_ACTOR`, then your user name | Who published it, recorded in the catalog. |

What it writes:

- the package, with the version and publish time stamped into its manifest, to
  `adapters-versions/{id}/{version}`;
- for a .NET adapter, unless `--no-promote`, the same package to `adapters/{id}`;
- the catalog entry `adapters-catalog/{id}.json`, with the new version, and as current unless
  `--no-promote`.

A Python, Node or `exec` package is never written to `adapters/{id}`: hosts from before manifests
would start whatever is there with `dotnet`. Newer hosts find its current version in the catalog.
For the same reason, an id once published as a .NET adapter cannot be published in another
runtime; use a new id.

```sh
sw-serverless publish bin/serverless/acme.orders-1.2.0.zip -p s3 -b my-adapters -u https://s3.example.com
sw-serverless publish bin/serverless/acme.orders.zip -v minor --no-promote --notes "Retries on 503"
```

A package you assembled yourself, such as an [`exec`](packaging-and-storage.md#exec) adapter, can
be published too, as long as it has an `adapter.json` with a valid `id`.

## promote

```sh
sw-serverless promote <id> <version> [storage options]
```

Makes a published version the current one. Rolling back is promoting an older version; nothing is
rebuilt.

It downloads the version, checks its SHA-256 against the one recorded when it was published
(refusing if they differ), and points the catalog at it. For a .NET adapter it also copies the
package over `adapters/{id}`, with the storage metadata older hosts read. A withdrawn or unknown
version is refused.

Hosts pick up the change once their cached lookup expires (`AdapterMetadataCacheDuration`, 5
minutes by default).

## versions

```sh
sw-serverless versions <id> [storage options]
```

Lists the published versions, newest first. `*` marks the current one.

```
acme.orders: current 1.2.0
  VERSION            PUBLISHED (UTC)   BY                   SHA256
* 1.2.0              2026-10-02 08:30  ci-bot               3f2a9c1b7d4e
  1.1.0              2026-09-20 14:05  ada                  91c0e4a2b8f3 withdrawn
  1.0.0              2026-09-01 10:00  ada                  0b7d14e6c2a9
```

For an adapter published before the catalog existed, it reads the packages and their metadata
instead, and says so.

## withdraw

```sh
sw-serverless withdraw <id> <version> [storage options]
```

Marks a version as withdrawn in the catalog. It stays listed, `promote` refuses it, and
applications listing versions should not offer it. The package is not deleted, and a host asked
for that exact version still runs it. The current version cannot be withdrawn; promote another one
first.

## Publishing a .NET project directly (the original form)

```sh
sw-serverless [storage options] [-v <version>] [-k <kinds>] [--no-promote] [--notes <text>]
              [--published-by <name>] [--no-probe] <project.csproj> <adapter-id>
```

The form the tool has always had, kept unchanged. It builds a .NET project with
`dotnet publish -c Release`, writes the manifest, zips and uploads it, in one step. It works with
every SDK version, including those before `--describe`.

| Option | Meaning |
|---|---|
| `project` | The `.csproj` file. |
| `adapter-id` | The id to publish under. Uppercase is lowered. |
| `-v`, `--version` | An explicit version or `major`, `minor`, `patch`. **Without `-v` the upload is unversioned:** only `adapters/{id}` is replaced, and the catalog's current version is cleared. |
| `--no-promote` | With `-v` only: publish the version without making it current. |
| `-k`, `--kind` | Kinds, comma separated (`processor,source`). Replaces the kinds from `adapter.json` and `[AdapterKind]`. |
| `--notes`, `--published-by` | As for [publish](#publish). |
| `--no-probe` | Do not start the built adapter to ask for its settings. |

The manifest is made from the `adapter.json` beside the project file, if there is one, and what
the tool reads from the built assembly: the entry assembly, the lifecycle (classic or resident),
the kinds, and the SDK version. If `adapter.json` lists no `properties`, the tool starts a classic
adapter and asks it for the settings it declares, unless `--no-probe`. If that fails, the publish
continues with a warning and no properties.

```sh
sw-serverless -p s3 -b my-adapters -u https://s3.example.com -v patch ./Acme/Acme.csproj acme.orders
```

## Storage

`publish`, `promote`, `versions`, `withdraw` and the original form all need to know where adapters
are stored. Each setting is taken from the first of: the command-line flag, the config file given
with `-c`, the environment variable.

| Flag | Environment variable | Meaning |
|---|---|---|
| `-p`, `--provider` | `SWSL_PROVIDER` | `s3` (S3 or S3-compatible; the default), `as` (Azure Blob Storage), `gc` (Google Cloud Storage), `oc` (Oracle Object Storage), `local` (a folder) |
| `-b`, `--bucketname` | `SWSL_BUCKET` | Bucket or container name |
| `-a`, `--accesskey` | `SWSL_ACCESS_KEY` | Access key |
| `-s`, `--secret` | `SWSL_SECRET_KEY` | Secret key |
| `-u`, `--url` | `SWSL_SERVICE_URL` | Service URL. For `local`, the folder the store is kept in. |
| (config file only) | `SWSL_REGION` | Region |
| `-c`, `--cloudfilesconfigpath` | | A JSON config file (below) |
| `--published-by` | `SWSL_PUBLISHED_BY` | Who is publishing (then `GITHUB_ACTOR`, then the user name) |

Google Cloud reads a service account from `SWSL_GC_PROJECT_ID`, `SWSL_GC_PRIVATE_KEY_ID`,
`SWSL_GC_PRIVATE_KEY`, `SWSL_GC_CLIENT_EMAIL`, `SWSL_GC_CLIENT_ID` and
`SWSL_GC_CLIENT_X509_CERT_URL`, or from the config file. `SWSL_GC_PRIVATE_KEY` may write its line
breaks as a literal `\n`, as they appear in a service-account JSON file. Oracle's settings
(`TenantId`, `UserId`, `FingerPrint`, `RSAKey`, `NamespaceName`) come only from the config file.

The config file holds the same settings under `CloudFiles`:

```json
{
  "CloudFiles": {
    "Provider": "gc",
    "BucketName": "my-adapters",
    "ProjectId": "my-project",
    "PrivateKeyId": "…",
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\n…\n-----END PRIVATE KEY-----\n",
    "ClientEmail": "publisher@my-project.iam.gserviceaccount.com",
    "ClientId": "…"
  }
}
```

Its other keys are `AccessKeyId`, `SecretAccessKey`, `ServiceUrl`, `Region`, `TenantId`, `UserId`,
`FingerPrint`, `RSAKey`, `NamespaceName` and `ClientX509CertUrl`.

In CI, keep keys in secrets and pass them as environment variables rather than flags, which end up
in shell history and process listings:

```sh
export SWSL_PROVIDER=s3 SWSL_BUCKET=my-adapters SWSL_SERVICE_URL=https://s3.example.com
export SWSL_ACCESS_KEY=… SWSL_SECRET_KEY=…
sw-serverless publish bin/serverless/acme.orders-1.2.0.zip
```

For development, `-p local` keeps the store in a folder. A host reads it with
`AddLocalTestsCloudFiles` and the same bucket name and folder (see
[Hosting adapters](hosting.md#storage)):

```sh
sw-serverless publish bin/serverless/greeter-0.1.0.zip -p local -b adapters-dev -u /tmp/swsl-store
```

The CLI always publishes under the root folder `adapters`. To publish under another root, use
`SW.Serverless.Tooling` from your own tool (see [Extending the tools](extending.md)).

## How versions are chosen

- An explicit version must be a semantic version (`1.4.0`, or a pre-release such as `1.5.0-rc.1`),
  must not exist already, and its `major.minor.patch` must be higher than every published release.
- `major`, `minor` and `patch` bump the highest published release: `1.4.2` becomes `2.0.0`,
  `1.5.0` or `1.4.3`. With no releases yet, the result is `1.0.0`. Pre-releases are not counted.
- A published version is never replaced.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | The command did what it was asked: the build succeeded, every check passed, the publish completed. Also for `--help`. |
| `1` | Anything else: a bad command line, an invalid id or manifest, a failed build, a failed conformance check, a failed call, a storage error. The output says what went wrong. |
