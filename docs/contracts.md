# Contracts

A **contract** is a JSON document in which an application says what its adapters must do: the
kinds of adapter it has, the commands it calls on each kind, the shape of their arguments and
results, and examples to call them with. Adapters declare which contracts and kinds they implement.
The conformance kit (`sw-serverless test`) then checks an adapter against the contract, by running
it.

SW-Serverless ships no contract. Each application writes its own. This page uses the sample
**orders** contract from the test suite
(`SW.Serverless.Installer.UnitTests/Contracts/`) as the worked example.

- [The contract document](#the-contract-document)
- [The orders example](#the-orders-example)
- [Declaring a contract in an adapter](#declaring-a-contract-in-an-adapter)
- [What the conformance kit checks](#what-the-conformance-kit-checks)
- [Running the checks](#running-the-checks)
- [Shipping your contract with your own tool](#shipping-your-contract-with-your-own-tool)
- [Versioning a contract](#versioning-a-contract)

## The contract document

| Field | Meaning |
|---|---|
| `contract` | The contract's name, e.g. `orders`. Matched without regard to case. |
| `version` | An integer, 1 or more. An adapter declares the name and the version it implements. |
| `description` | Free text. |
| `encoding` | Free text explaining the encoding rules to adapter authors. Not read by the kit. |
| `types` | The payload types, by name. |
| `types.<T>.schema` | A JSON Schema for the type: either inline, or the name of a schema file beside the contract. |
| `types.<T>.encoding` | `"string"` when the type travels as raw text rather than JSON. |
| `types.<T>.description` | Free text. |
| `kinds` | The kinds, by name. |
| `kinds.<K>.description` | Free text. |
| `kinds.<K>.session` | Present (as text describing the order) when the host calls this kind's methods as one ordered session. |
| `kinds.<K>.methods` | The commands the host calls on this kind, in order. |
| `methods[].name` | The command name. Case-sensitive. |
| `methods[].input` | The argument's type name, or `null` for no argument. |
| `methods[].output` | The result's type name, or `null` for no result. |
| `methods[].examples` | Example arguments, as JSON. Strings for a `"string"` type. |
| `methods[].destructive` | `true` when calling it changes the outside world in a way a test must not do by default (deleting a file at the source, say). |

Payloads follow the [encoding rules](writing-adapters.md#encoding): a string type is raw text,
anything else is JSON with property names exactly as the schema gives them.

## The orders example

An order-processing application has two kinds of adapter: a **processor** takes an order and says
whether it was accepted; a **source** is where orders come from, read in one session per run.

`orders-adapter-contract.v1.json`:

```json
{
  "contract": "orders",
  "version": 1,
  "description": "What an order-processing application calls on its adapters.",
  "encoding": {
    "string": "A string argument or result is the raw UTF-8 text, not a JSON string.",
    "object": "Any other argument or result is JSON, with property names exactly as its schema gives them.",
    "none": "A method with no argument receives an empty payload; one with no result returns an empty payload."
  },
  "types": {
    "Order": { "schema": "order.schema.json" },
    "Receipt": { "schema": "receipt.schema.json" },
    "OrderId": { "encoding": "string", "description": "An id a source gave in List, passed back exactly as given." },
    "OrderIdList": { "schema": { "type": "array", "items": { "type": "string" } } }
  },
  "kinds": {
    "processor": {
      "description": "Takes an order and says whether it was accepted.",
      "methods": [
        {
          "name": "Process",
          "input": "Order",
          "output": "Receipt",
          "examples": [
            { "OrderId": "SO-1001", "Lines": 2 }
          ]
        }
      ]
    },
    "source": {
      "description": "Where orders come from, read in one session per run.",
      "session": "Open, List, then for each order Fetch and — once it is safely taken — Remove, and finally Close, which is also called after a failure.",
      "methods": [
        { "name": "Open", "input": null, "output": null },
        { "name": "List", "input": null, "output": "OrderIdList" },
        { "name": "Fetch", "input": "OrderId", "output": "Order" },
        { "name": "Remove", "input": "OrderId", "output": null, "destructive": true },
        { "name": "Close", "input": null, "output": null }
      ]
    }
  }
}
```

`order.schema.json`, beside it:

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "Order",
  "type": "object",
  "required": ["OrderId"],
  "properties": {
    "OrderId": { "type": "string" },
    "Lines": { "type": "integer" }
  },
  "additionalProperties": true
}
```

`receipt.schema.json`:

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "Receipt",
  "type": "object",
  "required": ["Accepted"],
  "properties": {
    "Accepted": { "type": "boolean" },
    "Reference": { "type": ["string", "null"] }
  },
  "additionalProperties": true
}
```

## Declaring a contract in an adapter

An adapter declares the contract name and version, and the kinds it implements. These reach the
manifest through `--describe` when the adapter is built; you can also list them in `adapter.json`
under `contracts` and `kinds`.

A processor for the orders contract:

**.NET**

```csharp
using SW.Serverless.Sdk;

public class Order
{
    public string OrderId { get; set; }
    public int Lines { get; set; }
}

public class Receipt
{
    public bool Accepted { get; set; }
    public string Reference { get; set; }
}

[AdapterKind("processor")]
[AdapterContract("orders", 1)]
public class Processor
{
    public Processor() => Runner.Expect("Partner", description: "Who receives the orders.");

    public Task<Receipt> Process(Order order) =>
        Task.FromResult(order.Lines == 0
            ? new Receipt { Accepted = false }
            : new Receipt { Accepted = true, Reference = $"{Runner.StartupValueOf("Partner")}:{order.OrderId}" });
}

static class Program
{
    static Task Main() => Runner.Run(new Processor());
}
```

**Python**

```python
import sw_serverless as sw


@sw.implements("orders", 1, "processor")
class Processor:
    def __init__(self):
        sw.expect("Partner", description="Who receives the orders.")

    @sw.command("Process", description="Takes an order and says whether it was accepted.")
    def process(self, order: dict) -> dict:
        if not order.get("Lines"):
            return {"Accepted": False, "Reference": None}
        return {"Accepted": True, "Reference": f"{sw.value_of('Partner')}:{order['OrderId']}"}


if __name__ == "__main__":
    sw.run(Processor)
```

**Node**

```js
const sw = require("@simplyworks/sw-serverless");

class Processor {
  static kinds = ["processor"];
  static contracts = { orders: 1 };
  static commands = {
    Process: { method: "process", input: "json", output: "json", description: "Takes an order and says whether it was accepted." },
  };

  constructor() {
    sw.expect("Partner", { description: "Who receives the orders." });
  }

  process(order) {
    return order.Lines
      ? { Accepted: true, Reference: `${sw.valueOf("Partner")}:${order.OrderId}` }
      : { Accepted: false, Reference: null };
  }
}

sw.run(Processor);
```

Command names must match the contract exactly, including case.

An application can make this easier for its adapter authors by publishing base classes that carry
the declarations and the method names, in each language. In Python, `@sw.implements` on a base
class is inherited; in Node, `static kinds`, `contracts` and `commands` are merged down the class
chain; in .NET, `[AdapterKind]` and `[AdapterContract]` are inherited.

## What the conformance kit checks

`sw-serverless test` (and `ConformanceRunner` in `SW.Serverless.Tooling`) runs these checks, in
this order. Each is reported as `PASS`, `FAIL` or `SKIP`, and the run passes when nothing failed.

| Check | Passes when |
|---|---|
| `manifest` | The package has an `adapter.json` that can be read and passes [validation](manifest.md#validation). |
| `entry` | The manifest's entry for this platform exists in the package. Reported only on failure; nothing else runs after it fails. |
| `describe` | The adapter answers `--describe` with a description that names its SDK language. |
| `settings match the manifest` | Every setting the adapter declares is in the manifest's `properties` and the reverse, with the same `required`, `secret` and (for non-secrets) `default`. |
| `starts` | It installs and starts the way a host does (from a temporary store, through the real host library), as a classic session or a resident instance. Nothing else runs after it fails. |
| `contracts` | Skipped when it declares no contract. For each contract it declares (in the manifest or the description): the contract is known (given with `--contract` or registered), and the adapter implements at least one of its kinds. |
| `<contract> <kind>: methods` | Every method of the kind is one of the adapter's commands (case-sensitive). |
| `<contract> <kind>: <Method> answers example <n>` | For a kind without `session`: each example is sent; the call succeeds; if the method has an output with a schema, the answer is JSON valid against it. Skipped for a method with no examples. |
| `<contract> <kind>: <Method>` | For a kind with `session`: the methods are called once each, in the contract's order. A method without input is called with none. A method with input is given the first id from the first list a previous method returned; skipped if there was none. A `destructive` method is skipped unless `--allow-delete`. Outputs are checked against their schemas. |
| `an unknown command is refused` | Calling a command the adapter does not have fails with an error, and the adapter still answers afterwards. |

The kit really runs the adapter with the settings you give it, so it calls whatever they point at.
For a source, point it at test data.

## Running the checks

```sh
sw-serverless test ./OrdersProcessor --settings settings.json --contract contracts/orders-adapter-contract.v1.json
```

Schema files named in the contract are read from the contract file's folder. Several contracts go
after one flag: `--contract a.json b.json`.

For the orders processor above:

```
PASS manifest
PASS describe — python SDK 10.2.2, 1 commands
PASS settings match the manifest — 1 settings
PASS starts — classic session
PASS orders processor: methods — Process
PASS orders processor: Process answers example 1 — a valid Receipt
PASS an unknown command is refused — with an error, and it kept answering
Conforms.
```

If the adapter declares a contract the kit was not given:

```
FAIL contract orders v1 — the kit doesn't know this contract; pass it with --contract
```

From code:

```csharp
using SW.Serverless.Tooling.Conformance;

var report = await new ConformanceRunner().RunAsync(new ConformanceOptions
{
    PackageDirectory = "/build/acme.orders/package",       // an unpacked package
    Settings = new Dictionary<string, string> { ["Partner"] = "acme" },
    Contracts = { ContractDocument.FromFile("contracts/orders-adapter-contract.v1.json") },
    CommandTimeoutSeconds = 30,
    Log = Console.WriteLine,
});

foreach (var check in report.Checks)
    Console.WriteLine($"{check.Outcome} {check.Name} {check.Detail}");
return report.Passed ? 0 : 1;
```

## Shipping your contract with your own tool

The `sw-serverless` CLI knows no contracts, so adapter authors pass yours with `--contract`. An
application that ships its own tool built on `SW.Serverless.Tooling` can instead register its
contract once, so every adapter declaring it is checked without being handed the file:

```csharp
using System.Reflection;
using SW.Serverless.Tooling.Conformance;

static string Embedded(string name)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OrdersTool.Contracts.{name}")!;
    return new StreamReader(stream).ReadToEnd();
}

ContractDocument.Register(ContractDocument.FromJson(
    Embedded("orders-adapter-contract.v1.json"),
    readSibling: Embedded));            // how to read schema files the contract names
```

`ContractDocument.FromFile(path)` reads a contract from disk with its schemas beside it.
`ContractDocument.FromJson(json, readSibling)` reads one from a string, with `readSibling` reading
the schema files it names. `Register` replaces an earlier registration of the same name and
version. A contract passed in `ConformanceOptions.Contracts` is used before a registered one.

See [Extending the tools](extending.md) for building such a tool.

## Versioning a contract

The version is a whole number. When a change would break existing adapters (a renamed method, a
new required property in a result), publish the contract under the next version and keep the old
one: adapters keep declaring the version they implement, and your application can support both
for as long as it needs. Adding an optional property or a new kind usually does not need a new
version.
