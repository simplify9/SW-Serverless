# Extending the tools

Everything `sw-serverless` does is in the library `SimplyWorks.Serverless.Tooling`. An application
with adapters of its own can build its own command-line tool or server on it, so its adapter
authors get the application's templates, base classes and contract without extra steps, while the
build, the conformance kit and the storage layout stay exactly those of `sw-serverless`.

This page shows the pieces with an example: an "orders" application shipping an `orders-adapters`
tool.

- [Referencing the library](#referencing-the-library)
- [Templates: Scaffolder](#templates-scaffolder)
- [Your own packages: BuildRequest.Packages](#your-own-packages-buildrequestpackages)
- [Your contract: ContractDocument.Register](#your-contract-contractdocumentregister)
- [Running and testing: LocalAdapterHost and ConformanceRunner](#running-and-testing-localadapterhost-and-conformancerunner)
- [Publishing: PackagePublisher and AdapterRepository](#publishing-packagepublisher-and-adapterrepository)
- [Putting it together](#putting-it-together)

## Referencing the library

```sh
dotnet add package SimplyWorks.Serverless.Tooling
```

It brings `SimplyWorks.Serverless` (the host, which the conformance kit runs adapters on) and
`SimplyWorks.Serverless.Contract` (the manifest and catalog models), and the storage providers.

| Namespace | Types |
|---|---|
| `SW.Serverless.Tooling.Scaffolding` | `Scaffolder`, `ScaffoldRequest`, `ScaffoldResult` |
| `SW.Serverless.Tooling.Building` | `PackageBuilder`, `BuildRequest`, `BuildResult`, `VendoredPackage`, `IgnoreRules`, `SecretScanner` |
| `SW.Serverless.Tooling.Conformance` | `ConformanceRunner`, `ConformanceOptions`, `ConformanceReport`, `ContractDocument` |
| `SW.Serverless.Tooling` | `LocalAdapterHost`, `PackagePublisher`, `PublishPackageRequest`, `AdapterRepository`, `CloudFilesFactory` |
| `SW.Serverless.Installer` | `ServerlessUploadOptions` (the storage settings `CloudFilesFactory` takes) |
| `SW.Serverless.Contract.Catalog` | `AdapterManifest`, `AdapterCatalogEntry`, `AdapterCatalogStore`, `AdapterCatalogPaths`, `AdapterSelfDescription` |

## Templates: Scaffolder

`Scaffolder.Scaffold(request)` writes the generic greeter that `sw-serverless init` writes.
`Scaffolder.Scaffold(request, templates)` writes your files instead, after the same checks: a name
that can be a project and class name, a language the SDKs support (`dotnet`, `python`, `node`,
`typescript`), a valid id, and an empty folder.

A template set is a function from the name, id, language and kind to a list of files:

```csharp
public delegate IEnumerable<(string File, string Content)> Templates(string name, string id, string language, string kind);
```

`kind` is `ScaffoldRequest.Kind`, which the generic templates ignore and yours can use.

```csharp
using SW.Serverless.Tooling.Scaffolding;

static IEnumerable<(string File, string Content)> OrdersTemplates(string name, string id, string language, string kind)
{
    if (language != "python") yield break;   // this example only has Python templates

    yield return ("adapter.json", $$"""
        {
          "id": "{{id}}",
          "version": "0.1.0",
          "displayName": "{{Scaffolder.Spaced(name)}}",
          "runtime": "python",
          "entry": "main.py"
        }
        """);

    yield return ("main.py", kind == "source"
        ? $$"""
            import acme_orders
            import sw_serverless as sw


            class {{name}}(acme_orders.Source):
                def list(self) -> list[str]:
                    return []

                def fetch(self, order_id: str) -> dict:
                    return {"OrderId": order_id}


            if __name__ == "__main__":
                sw.run({{name}})
            """
        : $$"""
            import acme_orders
            import sw_serverless as sw


            class {{name}}(acme_orders.Processor):
                def process(self, order: dict) -> dict:
                    return {"Accepted": True, "Reference": order["OrderId"]}


            if __name__ == "__main__":
                sw.run({{name}})
            """);

    yield return ("requirements.txt", "acme-orders==1.0.0\n");
    yield return (".gitignore", "__pycache__/\nbin/\nsettings.json\n");
}

var result = Scaffolder.Scaffold(new ScaffoldRequest
{
    Name = "PartnerFeed",
    Language = "python",
    Kind = "source",
    ParentDirectory = ".",
}, OrdersTemplates);

foreach (var problem in result.Problems) Console.WriteLine(problem);
```

Helpers for writing templates: `Scaffolder.IdFrom(name)` (`AcmeOrders` to `acme.orders`),
`Scaffolder.Spaced(name)` (`Acme Orders`), `Scaffolder.NodePackageJson(id, typeScript, dependencies)`,
`Scaffolder.TsConfig`, and `Scaffolder.InLanguage(language)` for README text.

## Your own packages: BuildRequest.Packages

An application usually gives its adapter authors a small package of its own: base classes that
declare the contract's kinds and command names, and the payload types. For .NET this is a NuGet
package like any other. For Python and Node, which `sw-serverless build` builds without PyPI or
npm, the build can **vendor** it from files you hand it, exactly as it vendors the SDK:

```csharp
using SW.Serverless.Tooling.Building;

var request = new BuildRequest
{
    ProjectDirectory = "./PartnerFeed",
    Log = Console.WriteLine,
    Packages =
    {
        new VendoredPackage
        {
            Runtime = "python",
            Name = "acme-orders",                      // as requirements.txt names it
            Files = { ["acme_orders/__init__.py"] = File.ReadAllBytes("python/acme_orders/__init__.py") },
        },
        new VendoredPackage
        {
            Runtime = "node",
            Name = "@acme/orders",                     // as package.json names it
            Files =
            {
                ["@acme/orders/package.json"] = File.ReadAllBytes("node/package.json"),
                ["@acme/orders/index.js"] = File.ReadAllBytes("node/index.js"),
            },
        },
    },
};

var result = await PackageBuilder.BuildAsync(request);
if (!result.Succeeded) foreach (var problem in result.Problems) Console.Error.WriteLine(problem);
else Console.WriteLine($"Built {result.ZipPath}");
```

- `Files` are keyed by path under `_vendor/` (Python) or `node_modules/` (Node). A path that
  would land outside that folder is refused.
- `Name` is the name an adapter's `requirements.txt` or `package.json` uses for it. The build skips
  that name when it calls `pip` or `npm`, so naming it there (for editors and readers) does not
  send the build to a registry that does not have it.
- Packages for one runtime are ignored when building another, and for .NET.

The Python package above could be:

```python
# acme_orders/__init__.py
import sw_serverless as sw


@sw.implements("orders", 1, "processor")
class Processor:
    @sw.command("Process", description="Takes an order and says whether it was accepted.")
    def process(self, order: dict) -> dict:
        raise NotImplementedError


@sw.implements("orders", 1, "source")
class Source:
    @sw.command("Open")
    def open(self) -> None: ...

    @sw.command("List")
    def list(self) -> list[str]:
        raise NotImplementedError

    @sw.command("Fetch")
    def fetch(self, order_id: str) -> dict:
        raise NotImplementedError

    @sw.command("Remove")
    def remove(self, order_id: str) -> None: ...

    @sw.command("Close")
    def close(self) -> None: ...
```

A subclass overrides the methods; the command names, kinds and contract come from the base class.

Other `BuildRequest` options:

| Property | Default | Meaning |
|---|---|---|
| `ProjectDirectory` | (required) | The adapter's folder, with its `adapter.json`. |
| `OutputDirectory` | `<project>/bin/serverless` | Where `package/` and the zip go. |
| `IncludeSource` | `true` | Carry the source under `source/`. |
| `AllowedFiles` | empty | Source files whose secret-scan finding is a false positive. |
| `DryRun` | `false` | Collect and scan the source only. |
| `Runtimes` | `python3`, `node`, `dotnet` on the `PATH` | Where the build finds Python and Node. |
| `Log` | nothing | Progress lines. |

`BuildResult` has `Succeeded`, `Problems`, `Warnings`, `Manifest`, `PackageDirectory`, `ZipPath`,
`SourceFiles` and secret `Findings`.

## Your contract: ContractDocument.Register

Register your contract once when your tool starts, and the conformance kit checks every adapter
that declares it, without `--contract`:

```csharp
using SW.Serverless.Tooling.Conformance;

ContractDocument.Register(ContractDocument.FromJson(
    Embedded("orders-adapter-contract.v1.json"),
    readSibling: Embedded));   // reads order.schema.json and the other schema files it names
```

The contract format and what the kit checks are in [Contracts](contracts.md).

## Running and testing: LocalAdapterHost and ConformanceRunner

`LocalAdapterHost` runs an unpacked package on this machine exactly as a host would: it puts it in
a temporary local store, installs it from there, and starts it, classic or resident.

```csharp
using SW.Serverless.Tooling;

await using var adapter = await LocalAdapterHost.StartAsync(
    result.PackageDirectory,
    settings: new Dictionary<string, string> { ["Folder"] = "/data/test-orders" },
    commandTimeoutSeconds: 30);

Console.WriteLine(await adapter.CallAsync("List", null));        // the answer as text: raw string, or JSON
await adapter.CallVoidAsync("Close", null);

var (description, problem) = await LocalAdapterHost.DescribeAsync(
    Path.Combine(result.PackageDirectory, result.Manifest.Entry), result.Manifest.Runtime);
```

`ConformanceRunner` runs the checks `sw-serverless test` runs:

```csharp
var report = await new ConformanceRunner().RunAsync(new ConformanceOptions
{
    PackageDirectory = result.PackageDirectory,
    Settings = settings,
    AllowDelete = false,
    CommandTimeoutSeconds = 60,
    Log = Console.WriteLine,
});
```

`ConformanceOptions` also takes `Contracts` (used before registered ones), `WorkDirectory`,
`Runtimes` and `Limits`.

### Limits on what runs locally

A tool that builds and tries adapters on a shared server can cap them (10.2.1 and later). Pass
`LocalAdapterLimits` to `LocalAdapterHost.StartAsync` (`limits:`) or set
`ConformanceOptions.Limits`; leave it null, the default, for no limits.

```csharp
await using var adapter = await LocalAdapterHost.StartAsync(packageDirectory, settings,
    limits: new LocalAdapterLimits
    {
        MemoryLimitBytes = 256L * 1024 * 1024,              // hard: the process is killed above it
        CpuPercentLimit = 100.0 / Environment.ProcessorCount, // one core, as a share of the machine
    });
```

| Property | Default | Meaning |
|---|---|---|
| `MemoryLimitBytes` | `0` (none) | The host's hard memory limit. Also given to the runtime (see [Hosting](hosting.md#limits-and-supervision)), so on .NET, Node and Python on Linux an allocation past it usually fails inside the adapter first. |
| `CpuPercentLimit` | `0` (none) | Sustained CPU as a share of the whole machine. Over it for `CpuLimitSamples` samples in a row, the adapter is asked to stop, and killed if it hasn't within two samples (at least 2 seconds). |
| `CpuLimitSamples` | `3` | Samples in a row over `CpuPercentLimit` before it trips. |
| `SampleInterval` | 1 second | How often the process is sampled: the host's heartbeat. The missed heartbeats allowed grow to keep the default host's 45 seconds of tolerance. |

A call the adapter is running when it crosses a limit fails saying so, never with a bare closed
stream: an `AdapterStoppedException` (`LimitExceeded` is true), such as
`Adapter 'acme.orders/0.0.0' stopped: it was using 167 MB, over its memory limit of 150 MB, and
was killed.`, or the adapter's own out-of-memory error, such as
`MemoryError: the adapter ran out of memory: it runs under a memory limit of 150 MB`. Limits reach
every adapter the resident host runs, which is all of them except a .NET adapter on the protocol 1
text protocol.

## Publishing: PackagePublisher and AdapterRepository

Get an `ICloudFilesService` for your storage, either the one your application already registers,
or one built from settings:

```csharp
using SW.Serverless.Installer;    // ServerlessUploadOptions
using SW.Serverless.Tooling;

var files = CloudFilesFactory.Create(new ServerlessUploadOptions
{
    Provider = "s3",                        // s3, as, gc, oc or local
    BucketName = "orders-adapters",
    AccessKeyId = Environment.GetEnvironmentVariable("ADAPTERS_ACCESS_KEY"),
    SecretAccessKey = Environment.GetEnvironmentVariable("ADAPTERS_SECRET_KEY"),
    ServiceUrl = "https://s3.example.com",
});
```

Publish a built package. Unlike the CLI, you can choose the root folder in storage; hosts must be
configured with the same one (`ServerlessOptions.AdapterRemotePath`):

```csharp
var published = await PackagePublisher.PublishPackageAsync(files, new PublishPackageRequest
{
    PackagePath = result.ZipPath,
    Version = "minor",                      // or "1.4.0", or null for the manifest's version
    Promote = true,
    ReleaseNotes = "Retries on 503.",
    PublishedBy = "orders-adapters",
    RemotePath = "orders-adapters",         // the root folder; "adapters" unless set
}, log: Console.WriteLine);

Console.WriteLine($"{published.Manifest.Id} {published.Version} {published.Sha256}");
```

Manage versions with `AdapterRepository`, given the same root:

```csharp
var repository = new AdapterRepository(files, Console.WriteLine, root: "orders-adapters");

var listing = await repository.ListVersionsAsync("acme.orders");    // Current, Versions, FromCatalog
await repository.PromoteAsync("acme.orders", "1.3.0", workDirectory: Path.GetTempPath());
await repository.WithdrawAsync("acme.orders", "1.4.0");
var next = await repository.ResolveVersionAsync("acme.orders", "patch");

var entry = await repository.Catalog.GetAsync("acme.orders");      // the catalog entry
```

These are the operations behind `publish`, `promote`, `withdraw` and `versions`, with the same
rules (see [The CLI](cli.md) and [Packaging and storage](packaging-and-storage.md)). Failures the
caller can act on are thrown as `SWException` (from `SimplyWorks.PrimitiveTypes`) with a message
that says what to do; an invalid or existing version as `ArgumentException`.

## Putting it together

A minimal `orders-adapters` tool, with `init`, `build`, `test` and `publish`:

```csharp
using SW.Serverless.Installer;
using SW.Serverless.Tooling;
using SW.Serverless.Tooling.Building;
using SW.Serverless.Tooling.Conformance;
using SW.Serverless.Tooling.Scaffolding;

ContractDocument.Register(ContractDocument.FromJson(Embedded("orders-adapter-contract.v1.json"), Embedded));

switch (args[0])
{
    case "init":    // orders-adapters init <Name> <processor|source>
    {
        var made = Scaffolder.Scaffold(new ScaffoldRequest { Name = args[1], Kind = args[2], Language = "python" }, OrdersTemplates);
        made.Problems.ForEach(Console.WriteLine);
        return made.Succeeded ? 0 : 1;
    }
    case "build":   // orders-adapters build <folder>
    {
        var built = await PackageBuilder.BuildAsync(new BuildRequest { ProjectDirectory = args[1], Packages = { OrdersPython() }, Log = Console.WriteLine });
        built.Problems.ForEach(Console.WriteLine);
        return built.Succeeded ? 0 : 1;
    }
    case "test":    // orders-adapters test <unpacked package folder>
    {
        var report = await new ConformanceRunner().RunAsync(new ConformanceOptions { PackageDirectory = args[1], Log = Console.WriteLine });
        report.Checks.ForEach(c => Console.WriteLine($"{c.Outcome} {c.Name} {c.Detail}"));
        return report.Passed ? 0 : 1;
    }
    case "publish": // orders-adapters publish <zip>
    {
        var files = CloudFilesFactory.Create(new ServerlessUploadOptions { Provider = "local", BucketName = "dev", ServiceUrl = "/tmp/orders-store" });
        await PackagePublisher.PublishPackageAsync(files, new PublishPackageRequest { PackagePath = args[1], RemotePath = "orders-adapters" });
        return 0;
    }
    default:
        return 1;
}
```

`OrdersTemplates` is the template function from [Templates](#templates-scaffolder), `OrdersPython()`
returns the Python `VendoredPackage` from [Your own packages](#your-own-packages-buildrequestpackages),
and `Embedded` reads a resource embedded in the tool, as in
[Contracts](contracts.md#shipping-your-contract-with-your-own-tool).
