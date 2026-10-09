# SW-Serverless documentation

Start with [Concepts](concepts.md). It explains the words the other pages use. Then read the page
for what you are doing.

## If you are building an application that runs adapters

1. [Concepts](concepts.md): host, adapter, package, manifest, catalog, versions, lifecycles,
   protocols, runtimes, contracts, settings.
2. [Hosting adapters](hosting.md): adding the host library, classic sessions, resident adapters,
   the event sink and state store, storage, pinning versions, limits, what the host machine needs,
   security.
3. [Contracts](contracts.md): describing what your application expects of its adapters, and
   checking adapters against it.
4. [Extending the tools](extending.md): your own CLI or server on `SW.Serverless.Tooling`, with
   your own templates, packages and contracts.

## If you are writing adapters

1. [Concepts](concepts.md).
2. [Writing adapters](writing-adapters.md): settings, commands, encoding, errors, logging,
   resident hooks, the adapter context, `--describe`. .NET, Python and Node side by side.
3. [The CLI](cli.md): `init`, `build`, `test`, `run`, `publish` and the rest.
4. [The manifest](manifest.md): `adapter.json`.
5. [Packaging and storage](packaging-and-storage.md): what a package contains and where it goes.

## Reference

- [The CLI](cli.md): every command, flag, default, environment variable and exit code.
- [The manifest](manifest.md): every `adapter.json` field and its validation rules.
- [The protocol](protocol.md): the stdin handshake, the gRPC stream and its frames, the encoding
  rules, the `--describe` output. For writing an SDK in another language or an `exec` adapter.
- [Compatibility](compatibility.md): what keeps working with older hosts, SDKs and packages.

## Samples in this repository

| Project | What it shows |
|---|---|
| `SW.Serverless.Samples.Classic` | A classic .NET adapter: settings, typed commands, `AdapterLogger`, failures and timeouts. |
| `SW.Serverless.Samples.Ticker` | The smallest resident .NET adapter: attach, events, heartbeat status, commands that change it while it runs. |
| `SW.Serverless.Samples.FolderSource` | A resident adapter that reads files from a folder and publishes each as an event, archiving the file only after the host accepts it. |
| `SW.Serverless.Samples.LargeFiles` | Streaming a large file in chunks, with `AdapterHost` dependency injection. |
| `SW.Serverless.Samples.RabbitMq`, `.RabbitPublisher`, `.RabbitConsumer` | Resident adapters for a RabbitMQ broker: publishing with confirms, consuming with acknowledgement after the host accepts. |
| `SW.Serverless.Samples.Greedy` | An adapter that uses memory and CPU on demand, for exercising limits. |
| `SW.Serverless.SampleWeb` | A web host with a dashboard of resident adapters, events, logs and metrics. `dotnet run --project SW.Serverless.SampleWeb`. |
| `SW.Serverless.UnitTests/PythonAdapters`, `/NodeAdapters` | Small Python, JavaScript and TypeScript adapters, classic and resident, used by the tests. |
| `SW.Serverless.Installer.UnitTests/Contracts` | The sample "orders" contract used in [Contracts](contracts.md). |

The RabbitMQ samples need a broker, for example
`docker run -d --rm -p 5672:5672 rabbitmq:3.13-management`.

## Running the tests

```sh
dotnet test
```

The tests use a local-filesystem store, so they need no cloud account. Python and Node adapter
tests need `python3` and `node` on the `PATH`. The RabbitMQ tests start a broker with
Testcontainers and report Inconclusive without Docker; set `SWSL_SKIP_BROKER_TESTS=1` to skip them.
