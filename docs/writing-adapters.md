# Writing adapters

This page is for developers writing adapters. Every topic shows .NET, Python and Node
(JavaScript or TypeScript) side by side. Read [Concepts](concepts.md) first if the words setting,
command, classic or resident are new.

- [The SDKs](#the-sdks)
- [A first adapter](#a-first-adapter)
- [Settings](#settings)
- [Commands](#commands)
- [Encoding](#encoding)
- [Errors](#errors)
- [Logging](#logging)
- [Resident adapters](#resident-adapters)
- [The adapter context: events, state, metrics](#the-adapter-context-events-state-metrics)
- [Kinds and contracts](#kinds-and-contracts)
- [Dependency injection in .NET](#dependency-injection-in-net)
- [Describe](#describe)
- [Building, testing and running locally](#building-testing-and-running-locally)

## The SDKs

| Language | SDK | Requires |
|---|---|---|
| C#, F#, VB | NuGet `SimplyWorks.Serverless.Sdk`, namespace `SW.Serverless.Sdk` | .NET 10. Version 10.1.0 or later for `sw-serverless build`. |
| Python | `sw-serverless`, `import sw_serverless as sw` | Python 3.12 or later. |
| JavaScript, TypeScript | `@simplyworks/sw-serverless` | Node 22 or later. Building TypeScript needs Node 22.13 or later. |

The Python and Node SDKs are not on PyPI or npm yet. `sw-serverless build` copies the SDK into the
package itself, so a build does not need them there. They will be published to PyPI and npm later.
Until then, for your editor:

- Python: `export PYTHONPATH=<a clone of SW-Serverless>/sdk/python/src`.
- Node: `npm install --no-save <a clone of SW-Serverless>/sdk/node`. A plain `npm install` of a
  project made by `sw-serverless init` fails, because its `package.json` names the SDK, which the
  npm registry does not have yet. The build leaves the SDK out when it installs dependencies.

`sw-serverless init <Name> --lang dotnet|python|node|typescript` writes a working project to start
from. See [The CLI](cli.md#init).

## A first adapter

A classic adapter with one setting and two commands: one takes text, one takes a JSON object.

**.NET**

```csharp
using SW.Serverless.Sdk;

public class Numbers
{
    public int A { get; set; }
    public int B { get; set; }
}

public class Greeter
{
    public Greeter()
    {
        Runner.Expect("Greeting", "Hello", description: "What to say before the name.");
    }

    [AdapterCommand("Greets someone by name.")]
    public Task<string> Greet(string name) =>
        Task.FromResult($"{Runner.StartupValueOf("Greeting")}, {name}!");

    [AdapterCommand("Adds two numbers.")]
    public Task<int> Add(Numbers numbers) => Task.FromResult(numbers.A + numbers.B);
}

static class Program
{
    static Task Main() => Runner.Run(new Greeter());
}
```

**Python**

```python
from dataclasses import dataclass

import sw_serverless as sw


@dataclass
class Numbers:
    A: int
    B: int


class Greeter:
    def __init__(self):
        sw.expect("Greeting", "Hello", description="What to say before the name.")

    @sw.command("Greet", description="Greets someone by name.")
    def greet(self, name: str) -> str:
        return f"{sw.value_of('Greeting')}, {name}!"

    @sw.command("Add", description="Adds two numbers.")
    def add(self, numbers: Numbers) -> int:
        return numbers.A + numbers.B


if __name__ == "__main__":
    sw.run(Greeter)
```

**JavaScript**

```js
const sw = require("@simplyworks/sw-serverless");

class Greeter {
  static commands = {
    Greet: { method: "greet", input: "string", output: "string", description: "Greets someone by name." },
    Add: {
      method: "add",
      input: { type: "object", properties: { A: { type: "integer" }, B: { type: "integer" } }, required: ["A", "B"] },
      output: "json",
      description: "Adds two numbers.",
    },
  };

  constructor() {
    sw.expect("Greeting", { default: "Hello", description: "What to say before the name." });
  }

  greet(name) {
    return `${sw.valueOf("Greeting")}, ${name}!`;
  }

  add({ A, B }) {
    return A + B;
  }
}

sw.run(Greeter);
```

**TypeScript** is the same code with types. Node removes the types at build time rather than
compiling, so only syntax that can simply be erased is allowed: no `enum`, no `namespace`, no
constructor parameter properties. The `tsconfig.json` that `init` writes sets
`erasableSyntaxOnly` so your editor flags them, and its `package.json` sets `"type": "module"`
so `import` works.

```ts
import { expect, run, valueOf } from "@simplyworks/sw-serverless";

interface Numbers { A: number; B: number }

class Greeter {
  static commands = {
    Greet: { method: "greet", input: "string", output: "string", description: "Greets someone by name." },
    Add: { method: "add", input: "json", output: "json", description: "Adds two numbers." },
  };

  constructor() {
    expect("Greeting", { default: "Hello", description: "What to say before the name." });
  }

  greet(name: string): string {
    return `${valueOf("Greeting")}, ${name}!`;
  }

  add(numbers: Numbers): number {
    return numbers.A + numbers.B;
  }
}

run(Greeter);
```

A host calls them the same way whatever the language:

```csharp
await serverless.StartAsync("greeter", correlationId, new Dictionary<string, string> { ["Greeting"] = "Hi" });
var text = await serverless.InvokeAsync<string>("Greet", "Ada");             // "Hi, Ada!"
var sum = await serverless.InvokeAsync<int>("Add", new { A = 2, B = 3 });     // 5
```

## Settings

Declare every setting the adapter reads, once, when the adapter is built. Read values inside
commands.

| | .NET | Python | Node |
|---|---|---|---|
| Declare | `Runner.Expect(name, optional = false, isPrivate = false, description = null)` or `Runner.Expect(name, defaultValue, isPrivate = false, description = null)` | `sw.expect(name, default=None, *, required=None, secret=False, description=None, type="text")` | `expect(name, { default, required, secret, description, type })` |
| Read | `Runner.StartupValueOf(name)`, `Runner.StartupValueOf<T>(name)` | `sw.value_of(name, default=None)` | `valueOf(name, fallback)` |
| All values | `Runner.StartupValues` | `sw.startup_values()` | `startupValues()` |

- **Required.** In .NET a setting is required unless `optional: true` or it has a default. In Python
  and Node it is required unless it has a default or `required` is false.
- **Secret** (`isPrivate` in .NET). Applications mask it, and its default is never written into the
  manifest.
- **Default.** Returned when the host sends no value.
- **Type.** Python and Node accept `text`, `multiline`, `number`, `boolean`, `select` and `json`, to
  tell an application what kind of field to show. Values always arrive as strings. .NET settings are
  `text`; set another type for a .NET adapter in `adapter.json` (see [The manifest](manifest.md#properties)).
- **CorrelationId.** In a classic session the host also sends `CorrelationId`. In .NET it is also
  `Runner.CorrelationId`.
- **Per-call values.** A resident instance may receive values with each command
  (`properties` on the host's `InvokeAsync`). The read functions return a per-call value first, then
  the startup value, then the default.

**.NET: read settings in commands, not in the constructor.** With `Runner.Run(new Greeter())` the
handler is built before the host's values have been read, so a value read in the constructor is
only the declared default. Declare in the constructor, read in the commands. If the constructor
needs values, pass a factory, which is called after the values arrive:

```csharp
static Task Main() => Runner.Run(() => new Greeter(Runner.StartupValueOf("Greeting")));
```

**.NET resident adapters and declarations.** A resident adapter (and any .NET adapter on
`Runner.RunResident`) reports its settings to the host when it connects. With
`Runner.RunResident(new Handler())` the constructor has already run by then, so declarations in it
are reported. When the handler is built later, by `AdapterHost` (see
[Dependency injection](#dependency-injection-in-net)), make the `Runner.Expect` calls in `Main`
before running, or the host will not see them. `--describe`, and so the build, sees them either way.

## Commands

A command takes at most one argument and returns at most one result.

**.NET.** Every public instance method that returns `Task` or `Task<T>` and has at most one
parameter is a command, under its own name. Names are matched without regard to case.
`[AdapterCommand("...")]` adds a description; it is optional. Under `Runner.RunResident` a command
may also take a `CancellationToken` as its last parameter, and the methods `StartAsync`,
`StopAsync`, `GetStatusAsync` and `ResetAsync` are never commands.

**Python.** A method marked `@sw.command("Name", description=...)` is a command. Without a name,
the method's own name is used: `@sw.command` alone works too. Commands may be `def` or `async def`;
plain `def` commands run on a worker thread, so a slow one does not stop the adapter answering the
host. The type hints of the argument and result decide how they are decoded and what schema
`--describe` reports. A dataclass argument is built from a JSON object.

**Node.** Commands are listed in `static commands`, keyed by the name the host calls:

```js
static commands = {
  Name: { method: "methodName", input: "string", output: "json", description: "…" },
};
```

`input` and `output` are `"string"`, `"bytes"`, `"json"`, or a JSON Schema object (JSON described by
that schema). Leave `input` out for a command with no argument, and `output` out for one that
returns nothing. Methods may be async. Subclasses inherit and extend their base class's `commands`.

In Python and Node, command names are matched exactly, including case.

## Encoding

Arguments and results cross the process boundary as bytes. The rule is the same in every SDK:

| Value | On the wire |
|---|---|
| A string | Its raw UTF-8 text. Not a JSON string: `Ada`, not `"Ada"`. |
| Bytes (.NET `byte[]`, Python `bytes`, Node `"bytes"` / `Buffer`) | As they are. |
| Nothing (`null`, `None`, no argument, `Task`) | An empty payload. |
| Anything else | JSON. |

JSON property names are written as your types name them: .NET uses the property names as declared
(`OrderId`, not `orderId`), Python uses dataclass field names, Node uses your object's keys. When an
application's [contract](contracts.md) fixes the names, match them exactly.

Protocol 1 (.NET classic adapters on `Runner.Run`) differs slightly: numbers and booleans travel as
.NET writes them with `ToString()` (`True`, not `true`), and `byte[]` is sent as JSON (base64).

## Errors

Throw or raise to fail a command. The caller gets an error with a type, a message and a detail
(the stack trace).

| | Fail with a type you choose | Type otherwise |
|---|---|---|
| .NET | Throw your own exception class: the type is its full name, e.g. `Acme.Orders.RejectedException`. | The exception's full type name. |
| Python | `raise sw.AdapterError("no stock", type="Acme.Rejected")` | The exception class's name, prefixed with its module unless it is a built-in or defined in the main script. |
| Node | `throw new sw.AdapterError("no stock", { type: "Acme.Rejected" })` | The error's constructor name, e.g. `TypeError`. |

On the host, a protocol 2 error is an `AdapterInvocationException` whose `AdapterExceptionType` is
that type. A protocol 1 error is an `Exception` carrying the adapter's exception text. Either way the
adapter keeps running and can take the next command.

A command the adapter does not have fails with the type `MissingMethodException` (Python and
Node) or `System.MissingMethodException` (.NET on protocol 2).

## Logging

| | How | Reaches the host as |
|---|---|---|
| .NET classic | `AdapterLogger.LogInformation(...)`, `LogWarning`, `LogError` | Log entries in `serverless.adapters.{id}` |
| .NET resident | `context.LogInformation(...)`, `LogWarning`, `LogError`, `Log(level, ...)`, or `ILogger<T>` with [dependency injection](#dependency-injection-in-net) | The same, with structured properties |
| Python | The standard `logging` module | The same |
| Node | `sw.log.trace`, `debug`, `info`, `warn`, `error`, `critical` (`message`, optional `error`) | The same |

- Do not write your own output to standard output. Protocol 1 uses it for results.
- In .NET resident adapters `AdapterLogger` writes to standard error, which the host keeps only for
  crash reports. Use the context or `ILogger` there.
- Python's root logger level is set to `INFO` if it was unset or higher, so `logging.info(...)` is
  sent by default.
- The host can change the lowest level an instance sends at run time. In .NET check
  `context.MinimumLogLevel` before building an expensive message.
- Logs are dropped, not queued without limit, if the adapter writes them faster than they can be
  sent. Command results and events are never dropped.

## Resident adapters

A resident adapter has a start hook, and optional stop, status and reset hooks.

| Hook | .NET (`IResidentAdapter`, `IResettable`) | Python | Node |
|---|---|---|---|
| Start | `Task StartAsync(IAdapterContext context, CancellationToken ct)` | `start(self)` | `start()` |
| Stop | `Task StopAsync(CancellationToken ct)` | `stop(self)` | `stop()` |
| Status | `Task<AdapterStatus> GetStatusAsync()` | `status(self)` returning a dict | `status()` returning an object |
| Reset | `Task ResetAsync(string sessionId)` | `reset(self, session_id)` | `reset(sessionId)` |
| Entry point | `Runner.RunResident(handler)` | `sw.run(Adapter)` (resident because it has `start`) | `run(Adapter)` (resident because it has `start`) |

Python and Node hooks may be sync or async.

- **Start** is called once the host has sent the settings. Open connections, start your own loop
  on a task of its own, and return. Start may ask the host for things — read state, publish an
  event — and commands wait until it has returned. If it fails, the adapter stops, and the host
  restarts it as it does after a crash. A stop that arrives while start is still running waits for
  it first, within the stop's own deadline.
- **Stop** is called when the host stops the adapter. Stop fetching, finish or hand back what is in
  flight, close connections. The host waits up to 30 seconds when it asked for a drain, 5 otherwise.
  Commands still running are allowed to finish within the same time.
- **Status** is called on every heartbeat and must return quickly. It reports `connected`, a free
  text `state` such as `Connected`, `Idle` or `Disconnected`, the number of items in flight, the
  last error, the time of the last message, and provider details as name-value pairs. Python keys: `connected`, `state`,
  `in_flight`, `last_error`, `last_message_unix_ms`, `details`. Node keys: `connected`, `state`,
  `inFlight`, `lastError`, `lastMessageOn`, `details`.
- **Reset** is for pooled adapters: forget anything kept for the session that ends.
- Commands work exactly as in classic adapters, and several may run at once.

An example: an adapter that publishes a numbered tick every few seconds, keeps the count in host
state so it survives restarts, and reports it in its status.

**.NET**

```csharp
using System.Text;
using SW.Serverless.Sdk;
using SW.Serverless.Sdk.Resident;

public class Ticker : IResidentAdapter
{
    IAdapterContext context;
    CancellationTokenSource stopping;
    Task loop;
    long sent;

    public Ticker() => Runner.Expect("IntervalSeconds", "5", description: "Seconds between ticks.");

    public Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
    {
        this.context = context;
        stopping = new CancellationTokenSource();
        loop = Task.Run(() => TickAsync(stopping.Token));   // not awaited: start must return
        return Task.CompletedTask;
    }

    async Task TickAsync(CancellationToken ct)
    {
        sent = long.Parse(await context.GetStateAsync("sent", ct) ?? "0");
        var interval = TimeSpan.FromSeconds(int.Parse(context.StartupValueOf("IntervalSeconds")));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            var number = sent + 1;
            var result = await context.PublishAsync(Encoding.UTF8.GetBytes($"tick {number}"),
                dedupeKey: $"tick-{number}", contentType: "text/plain", cancellationToken: ct);
            if (!result.Accepted) continue;          // not stored: send the same number again
            sent = number;
            await context.SetStateAsync("sent", sent.ToString(), ct);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    public Task<AdapterStatus> GetStatusAsync() =>
        Task.FromResult(new AdapterStatus { Connected = true, State = "Ticking", Details = { ["sent"] = sent.ToString() } });

    [AdapterCommand("Ticks published since the adapter was first started.")]
    public Task<long> Sent() => Task.FromResult(sent);
}

static class Program
{
    static Task Main() => Runner.RunResident(new Ticker());
}
```

**Python**

```python
import asyncio

import sw_serverless as sw


class Ticker:
    def __init__(self):
        sw.expect("IntervalSeconds", "5", description="Seconds between ticks.")
        self.sent = 0
        self.loop = None

    async def start(self):
        self.loop = asyncio.create_task(self.tick(sw.context()))   # not awaited: start must return

    async def tick(self, ctx):
        self.sent = int(await ctx.get_state("sent") or 0)
        interval = float(sw.value_of("IntervalSeconds"))
        while True:
            await asyncio.sleep(interval)
            number = self.sent + 1
            try:
                await ctx.publish(f"tick {number}", dedupe_key=f"tick-{number}", content_type="text/plain")
            except sw.AdapterError:
                continue                     # not stored: send the same number again
            self.sent = number
            await ctx.set_state("sent", str(self.sent))

    async def stop(self):
        self.loop.cancel()

    def status(self):
        return {"connected": True, "state": "Ticking", "details": {"sent": str(self.sent)}}

    @sw.command("Sent", description="Ticks published since the adapter was first started.")
    def sent_count(self) -> int:
        return self.sent


if __name__ == "__main__":
    sw.run(Ticker)
```

**Node**

```js
const sw = require("@simplyworks/sw-serverless");

class Ticker {
  static commands = {
    Sent: { method: "sentCount", output: "json", description: "Ticks published since the adapter was first started." },
  };

  constructor() {
    sw.expect("IntervalSeconds", { default: "5", description: "Seconds between ticks." });
    this.sent = 0;
    this.stopped = false;
  }

  start() {
    this.loop = this.tick(sw.context());   // not awaited: start must return
  }

  async tick(ctx) {
    try {
      this.sent = Number((await ctx.getState("sent")) ?? 0);
      const interval = Number(sw.valueOf("IntervalSeconds")) * 1000;
      while (!this.stopped) {
        await new Promise((resolve) => setTimeout(resolve, interval));
        const number = this.sent + 1;
        try {
          await ctx.publish(`tick ${number}`, { dedupeKey: `tick-${number}`, contentType: "text/plain" });
        } catch (e) {
          continue;                          // not stored: send the same number again
        }
        this.sent = number;
        await ctx.setState("sent", String(this.sent));
      }
    } catch (e) {
      if (!this.stopped) sw.log.error("ticking stopped", e);
    }
  }

  stop() {
    this.stopped = true;
  }

  status() {
    return { connected: true, state: "Ticking", details: { sent: this.sent } };
  }

  sentCount() {
    return this.sent;
  }
}

sw.run(Ticker);
```

### A .NET adapter on protocol 2, run as a classic session

A .NET handler run with `Runner.RunResident` that does not implement `IResidentAdapter` speaks
protocol 2 but has nothing to keep running. Put `"lifecycle": "classic"` in its `adapter.json`, and
`sw-serverless build` marks it as a classic adapter on protocol 2: hosts run it one session at a
time, as they do Python and Node classic adapters. Such hosts need `AddResidentAdapters`.

## The adapter context: events, state, metrics

The context is what a protocol 2 adapter can reach beyond its argument.

| | .NET | Python | Node |
|---|---|---|---|
| Get it | The `IAdapterContext` passed to `StartAsync`, or injected | `sw.context()` | `sw.context()` |
| Publish an event | `await context.PublishAsync(payload, dedupeKey, endpoint, headers, contentType, ct)` returns `PublishResult { Accepted, Reference, Error }` | `await ctx.publish(payload, dedupe_key=, content_type=, headers=, endpoint=)` returns the reference, raises `AdapterError` if rejected | `await ctx.publish(payload, { dedupeKey, contentType, headers, endpoint })` returns the reference, throws `AdapterError` if rejected |
| Read state | `await context.GetStateAsync(name)` (null if none) | `await ctx.get_state(name)` (None if none) | `await ctx.getState(name)` (null if none) |
| Write state | `await context.SetStateAsync(name, value)` | `await ctx.set_state(name, value)` | `await ctx.setState(name, value)` |
| Delete state | `await context.SetStateAsync(name, null)` | `await ctx.delete_state(name)` | `await ctx.deleteState(name)` |
| Record a metric | `context.Metric(name, value, tags)` | `ctx.metric(name, value, tags)` | `ctx.metric(name, value, tags)` |
| Read a value | `context.ValueOf(name)`, `context.InvocationValues` | `ctx.value_of(name)` | `ctx.valueOf(name)` |
| The call was abandoned | `context.CallCancelled`, or a `CancellationToken` parameter | `ctx.cancelled` (an `asyncio.Event`); an async command's task is also cancelled | `ctx.signal` (an `AbortSignal`) |
| The adapter is stopping | `context.Stopping` | `ctx.stopping` (an `asyncio.Event`) | `ctx.stopping` (an `AbortSignal`) |
| Identity | `context.AdapterId`, `context.InstanceKey` | `ctx.adapter_id`, `ctx.instance_key`, `ctx.session_id`, `ctx.command` | `ctx.adapterId`, `ctx.instanceKey`, `ctx.sessionId`, `ctx.command` |

**Events.** Publish, wait for the result, and acknowledge your source (delete the file, ack the
message) only when the host has accepted it. If the host rejects it, or the adapter crashes first,
the source still has it and it will be delivered again, so give every event a `dedupeKey` that
identifies it: a message id, an offset, a file name and its hash. `endpoint` says where it came
from: a queue, a topic, a folder. A payload is encoded like a command result: a string as UTF-8,
bytes as they are, anything else as JSON (in .NET, pass bytes). One event may be up to about 63 MB. The
host lets an instance have a limited number of events waiting at once (`MaxInFlight`, 16 by
default); further publishes wait their turn.

**State** is a small string per name, kept by the host for this adapter instance, and kept across
restarts. Use it for a bookmark, such as a cursor or the time of the last run, and write it only
once the work it records has been accepted. It is not a data store; the host may refuse a large
value, and the error reaches you.

**Metrics** are added to a counter of that name on the host.

**A synchronous Python command** cannot be stopped from outside. When the call is abandoned it
keeps running in its thread; check `ctx.cancelled` in long loops.

## Kinds and contracts

If an application's contract defines kinds, declare which you implement. Details and the full
example are in [Contracts](contracts.md).

| | Declare |
|---|---|
| .NET | `[AdapterKind("processor")]` and `[AdapterContract("orders", 1)]` on the handler class |
| Python | `@sw.implements("orders", 1, "processor")` on the class |
| Node | `static kinds = ["processor"];` and `static contracts = { orders: 1 };` on the class |
| Any language | `"kinds"` and `"contracts"` in `adapter.json` |

## Dependency injection in .NET

For a .NET adapter bigger than one class, `AdapterHost` (namespace `SW.Serverless.Sdk.Hosting`)
builds a service container once the settings have arrived, so constructor injection of
configuration is safe:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SW.Serverless.Sdk.Hosting;

static Task Main() => AdapterHost.CreateBuilder()
    .ConfigureServices((configuration, services) =>
    {
        services.Configure<PartnerOptions>(configuration);   // bound from the settings
        services.AddSingleton<IPartnerClient, PartnerClient>();
    })
    .Build<OrdersHandler>()
    .RunResidentAsync();                                     // or .RunAsync() for classic
```

The container provides:

- `IConfiguration`: the settings, then the package's storage metadata under `AdapterValues:`, then
  environment variables that start with `SWSL_` (prefix removed). Settings win.
- `ILogger<T>`, sent to the host's logs.
- `IAdapterContext`, under `RunResidentAsync`.
- The handler, as a singleton.

`AdapterSession.Id` and `AdapterSession.Command` give the current call's session id and command
name anywhere in the call.

Under `RunResidentAsync`, make the `Runner.Expect` calls in `Main`, before
`AdapterHost.CreateBuilder()`: the adapter reports its settings to the host before the container
builds the handler. See [Settings](#settings).

## Describe

Every SDK answers `--describe`: it prints a JSON description of the adapter and exits, without a
host. `sw-serverless build` uses it to write the manifest, and the conformance kit uses it to check
the adapter. Try it on a built package:

```sh
dotnet bin/serverless/package/Greeter.dll --describe
python3 bin/serverless/package/_serverless_entry.py --describe
node bin/serverless/package/main.js --describe
```

To describe the adapter, the SDK builds it without settings. If the constructor fails without
them, the description is still printed, with a warning that settings declared later may be missing.
Keep constructors free of work that needs settings. The output format is in
[The protocol](protocol.md#describe).

## Building, testing and running locally

```sh
sw-serverless build                                   # the package: bin/serverless/{id}-{version}.zip
sw-serverless test --settings settings.json           # runs it as a host would and checks it
sw-serverless run --settings settings.json --call Add --input '{"A":2,"B":3}'
```

`test` and `run` start the adapter on this machine through a real host, so they need the adapter's
runtime installed. `settings.json` is a flat JSON object of setting names to values. Keep it out of
version control once it holds real credentials; the `.gitignore` that `init` writes already does.
See [The CLI](cli.md).
