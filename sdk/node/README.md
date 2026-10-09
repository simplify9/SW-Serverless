# @simplyworks/sw-serverless

Write SW-Serverless adapters in JavaScript or TypeScript, on Node 22 or later. No dependencies: Node's
own `http2` speaks the host's protocol — gRPC over a Unix socket — and the SDK carries the protobuf
messages itself, so it vendors into an adapter package as plain files.

```js
const sw = require("@simplyworks/sw-serverless");

class Greeter {
  static commands = {
    Greet: { method: "greet", input: "string", output: "string", description: "Greets someone" },
  };

  constructor() {
    sw.expect("Greeting", { default: "Hello", description: "What to say" });
    sw.expect("ApiKey", { secret: true });
  }

  greet(name) {
    return `${sw.valueOf("Greeting")}, ${name}`;
  }
}

sw.run(Greeter);
```

- **Settings** are declared with `sw.expect(name, { default, required, secret, description, type })`
  and read with `sw.valueOf(name)`.
- **Commands** are declared in `static commands`: the method that runs each, and its `input` and
  `output` — `"string"` (raw text), `"bytes"`, `"json"`, or a JSON Schema. Leave `input` out for a
  command with no argument, and `output` for one that returns nothing. Methods may be async.
- **Errors** reach the caller with their type and message; throw
  `new sw.AdapterError(message, { type: "Acme.Rejected" })` to choose the type.
- **Resident adapters** have a `start` method and run until stopped, with optional `stop`, `status`
  and `reset(sessionId)`. `sw.context()` publishes events, keeps small state and records metrics;
  its `signal` aborts when the host gives up on a call.
- **Logs** go to the host with `sw.log.info(...)` and the other levels.
- `node main.js --describe` prints what the adapter is; `sw-serverless build` writes it into the manifest.

An application with a contract of its own declares it on the adapter class with `static kinds` and
`static contracts` (`{ orders: 1 }`), and can give its adapter authors base classes that do it for them.
`sw-serverless init --lang node` (or `typescript`) starts an adapter. Tests: `npm test`.
