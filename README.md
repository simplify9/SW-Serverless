# SW-Serverless

[![NuGet - SimplyWorks.Serverless](https://img.shields.io/nuget/v/SimplyWorks.Serverless.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless)
[![NuGet - SimplyWorks.Serverless.Sdk](https://img.shields.io/nuget/v/SimplyWorks.Serverless.Sdk.svg)](https://www.nuget.org/packages/SimplyWorks.Serverless.Sdk)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

SW-Serverless runs small programs, called **adapters**, as separate child processes of a host
application. The host installs an adapter from cloud storage by its id, starts it, calls its
commands by name, and stops it. Adapters can be written in .NET, Python, JavaScript or TypeScript,
or be any self-contained binary that speaks the protocol. The repository also has the tools to
create, build, test, version and publish adapters: the `sw-serverless` command-line tool and the
library behind it.

## When to use it

Use SW-Serverless when your application needs pieces of code that:

- are added or updated without redeploying the application: publish a new adapter version to
  storage, and the host installs it the next time it is asked for;
- must not take the application down when they fail, leak memory or hang: each adapter is its own
  process, with timeouts, and resident adapters are supervised and restarted;
- are written by other teams or partners, possibly in other languages, against a contract your
  application defines.

Typical examples are integrations with outside systems: one adapter per partner API, file format
or message broker.

It is not a general function-as-a-service platform. There is no HTTP gateway, autoscaling or
multi-node scheduler: adapters run on the machine that runs the host.

## The parts

| Part | Package | What it is for |
|---|---|---|
| Host library | NuGet `SimplyWorks.Serverless` (`SW.Serverless`) | Add to the application that runs adapters: `AddServerless`, `IServerlessService`, `AddResidentAdapters`, `IResidentAdapterHost`. |
| .NET SDK | NuGet `SimplyWorks.Serverless.Sdk` (`SW.Serverless.Sdk`) | Write an adapter in .NET: `Runner.Run`, `Runner.RunResident`, `Runner.Expect`. |
| Python SDK | `sw-serverless` (`import sw_serverless`), in `sdk/python` | Write an adapter in Python 3.12 or later. No dependencies. |
| Node SDK | `@simplyworks/sw-serverless`, in `sdk/node` | Write an adapter in JavaScript or TypeScript on Node 22 or later. No dependencies. |
| Contract | NuGet `SimplyWorks.Serverless.Contract` (`SW.Serverless.Contract`) | The gRPC protocol (`adapter.proto`), the manifest (`adapter.json`) and catalog models. Shared by everything else. |
| Tooling | NuGet `SimplyWorks.Serverless.Tooling` (`SW.Serverless.Tooling`) | What the CLI does, as a library: build, conformance tests, scaffolding, publishing. For an application that builds its own tools. |
| CLI | `sw-serverless` (project `SW.Serverless.Installer`) | `init`, `build`, `test`, `run`, `manifest validate`, `publish`, `promote`, `versions`, `withdraw`. |

The Python and Node SDKs are not on PyPI or npm yet. You do not need them there:
`sw-serverless build` copies the SDK into every Python and Node package it builds. They will be
published to PyPI and npm later.

## Install the CLI

Self-contained binaries are published on the
[GitHub releases](https://github.com/simplify9/SW-Serverless/releases) tagged `cli-v<version>`, one
per platform: `sw-serverless-<rid>.tar.gz` for `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`,
`osx-x64` and `osx-arm64`, and `sw-serverless-win-x64.zip`, with a `SHA256SUMS` file. On Linux or
macOS, the install script picks your platform, checks the download and installs to `~/.local/bin`:

```sh
curl -fsSL https://raw.githubusercontent.com/simplify9/SW-Serverless/main/scripts/install-cli.sh | sh
```

`INSTALL_DIR` changes where it goes; `SW_SERVERLESS_VERSION=10.2.0` picks a version. On Windows,
download `sw-serverless-win-x64.zip` from the release and put `sw-serverless.exe` on your `PATH`.

Or build it from source with the .NET 10 SDK:

```sh
dotnet run --project SW.Serverless.Installer -- <args>                 # run without installing
dotnet publish SW.Serverless.Installer -c Release -o ./out              # ./out/sw-serverless
```

See [docs/cli.md](docs/cli.md) for every command.

## Quick start: an adapter

`sw-serverless init` writes a small working adapter with two settings and two commands. The same
five commands then build, check, call and publish it, whatever its language.

```sh
sw-serverless init Greeter                       # .NET; or --lang python, node, typescript
cd Greeter
sw-serverless build                              # -> bin/serverless/greeter-0.1.0.zip
cp settings.example.json settings.json
sw-serverless test --settings settings.json      # runs it as a host would and checks it
sw-serverless run --settings settings.json --call Greet --input Ada
# Hello, Ada!
sw-serverless publish bin/serverless/greeter-0.1.0.zip -p local -b adapters-dev -u /tmp/swsl-store
```

`-p local` publishes to a folder, which is handy for trying things out. For S3, Azure, Google Cloud
or Oracle storage, see [storage providers](docs/cli.md#storage).

What `init` writes, in each language:

**.NET** (`Program.cs`)

```csharp
using SW.Serverless.Sdk;

public class Adapter
{
    public Adapter()
    {
        // Declare settings here; read them in the commands, never in the constructor.
        Runner.Expect("Greeting", "Hello", description: "What to say before the name.");
        Runner.Expect("ApiKey", optional: true, isPrivate: true, description: "A key, to show how a secret is declared.");
    }

    [AdapterCommand(Description = "Greets someone by name.")]
    public Task<string> Greet(string name) =>
        Task.FromResult($"{Runner.StartupValueOf("Greeting")}, {name}!");
}

static class Program
{
    static Task Main() => Runner.Run(new Adapter());
}
```

**Python** (`main.py`)

```python
import sw_serverless as sw


class Greeter:
    def __init__(self):
        sw.expect("Greeting", "Hello", description="What to say before the name.")
        sw.expect("ApiKey", secret=True, required=False, description="A key, to show how a secret is declared.")

    @sw.command("Greet", description="Greets someone by name.")
    def greet(self, name: str) -> str:
        return f"{sw.value_of('Greeting')}, {name}!"


if __name__ == "__main__":
    sw.run(Greeter)
```

**TypeScript** (`main.ts`; JavaScript is the same without the types, using `require`)

```ts
import { expect, run, valueOf } from "@simplyworks/sw-serverless";

class Greeter {
  static commands = {
    Greet: { method: "greet", input: "string", output: "string", description: "Greets someone by name." },
  };

  constructor() {
    expect("Greeting", { default: "Hello", description: "What to say before the name." });
    expect("ApiKey", { secret: true, required: false, description: "A key, to show how a secret is declared." });
  }

  greet(name: string): string {
    return `${valueOf("Greeting")}, ${name}!`;
  }
}

run(Greeter);
```

More in [docs/writing-adapters.md](docs/writing-adapters.md).

## Quick start: a host

Add the host library and a storage provider to your application:

```sh
dotnet add package SimplyWorks.Serverless
dotnet add package SimplyWorks.CloudFiles.LocalTests.Extensions   # or .S3.Extensions, .AS.Extensions, ...
```

```csharp
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless;
using SW.Serverless.Resident;

var builder = WebApplication.CreateBuilder(args);

// Where adapters are published: the same store the CLI published to above.
builder.Services.AddLocalTestsCloudFiles(o =>
{
    o.BucketName = "adapters-dev";
    o.StoragePath = "/tmp/swsl-store";
});

builder.Services.AddServerless();
// Needed for Python, Node and exec adapters, and for any resident adapter.
builder.Services.AddResidentAdapters<IgnoreEvents>();

var app = builder.Build();

app.MapGet("/greet/{name}", async (string name, IServiceProvider services) =>
{
    using var scope = services.CreateScope();          // the session ends with the scope
    var serverless = scope.ServiceProvider.GetRequiredService<IServerlessService>();
    await serverless.StartAsync("greeter", correlationId: Guid.NewGuid().ToString(),
        new Dictionary<string, string> { ["Greeting"] = "Hi" });
    return await serverless.InvokeAsync<string>("Greet", name);
});

app.Run();

// Receives events that resident adapters publish. This one accepts and drops them.
class IgnoreEvents : IAdapterEventSink
{
    public Task<EventOutcome> OnEventAsync(InboundEvent inboundEvent, CancellationToken cancellationToken) =>
        Task.FromResult(EventOutcome.Ok("ignored"));
}
```

The host machine needs the runtimes its adapters use on the `PATH`: `dotnet` for .NET adapters,
`python3` (3.12 or later) for Python, `node` (22 or later) for JavaScript and TypeScript. More in
[docs/hosting.md](docs/hosting.md).

## Documentation

- [Documentation index](docs/README.md)
- [Concepts](docs/concepts.md): host, adapter, package, manifest, versions, lifecycles, protocols, contracts
- [Hosting adapters](docs/hosting.md): the host library, sessions, resident adapters, limits, security
- [Writing adapters](docs/writing-adapters.md): .NET, Python and Node side by side
- [The CLI](docs/cli.md): every command, flag and environment variable
- [The manifest](docs/manifest.md): `adapter.json`, field by field
- [Packaging and storage](docs/packaging-and-storage.md): what a package holds and where it is stored
- [Contracts](docs/contracts.md): defining what adapters for your application must do, and testing it
- [The protocol](docs/protocol.md): for SDKs in other languages and `exec` adapters
- [Extending the tools](docs/extending.md): your own CLI or server on `SW.Serverless.Tooling`
- [Compatibility](docs/compatibility.md): what stays working across versions

## License

MIT. See [LICENSE](LICENSE).
