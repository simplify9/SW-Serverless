"use strict";
const test = require("node:test");
const assert = require("node:assert");
const sw = require("../src");
const { wire, decodeArgument, encodeResult, commandsOf } = sw._internal;

test("a frame round-trips with every kind of field", () => {
  const frame = {
    id: 42, hello: {
      token: "t", protocol_version: 2, capabilities: ["cancel", "command:Greet"],
      commands: [{ name: "Greet", returns_value: true, input_schema: '{"type":"string"}' }],
      settings: [{ name: "Url", required: true, secret: true }], kinds: ["handler"], contracts: { bitween: 1 },
    },
  };
  assert.deepStrictEqual(wire.decode("AdapterFrame", wire.encode("AdapterFrame", frame)), frame);
});

test("the bytes are the Python SDK's, and .NET's protobuf", () => {
  // Written by simplyworks_serverless._wire for the same frame.
  const frame = { id: -7, metric: { name: "m", value: 2.5, tags: { a: "b" } } };
  assert.strictEqual(wire.encode("AdapterFrame", frame).toString("hex"),
    "08f9ffffffffffffffff013a140a016d1100000000000004401a060a0161120162");
});

test("an empty message is present, and unknown fields are skipped", () => {
  assert.deepStrictEqual(wire.decode("HostFrame", Buffer.from([0x2a, 0x00])), { ping: {} });
  const unknown = Buffer.concat([Buffer.from([0x9a, 0x06, 0x03]), Buffer.from("abc"), wire.encode("HostFrame", { id: 3 })]);
  assert.deepStrictEqual(wire.decode("HostFrame", unknown), { id: 3 });
});

test("strings are raw text, bytes raw, and anything else JSON", () => {
  assert.strictEqual(encodeResult("hé").toString("hex"), "68c3a9");
  assert.strictEqual(decodeArgument("string", Buffer.from("hé")), "hé");
  assert.deepStrictEqual(decodeArgument("json", Buffer.from('{"a":1}')), { a: 1 });
  assert.deepStrictEqual(decodeArgument("bytes", Buffer.from([1, 2])), Buffer.from([1, 2]));
  assert.strictEqual(encodeResult(undefined).length, 0);
  assert.strictEqual(encodeResult({ toWire: () => ({ X: 1 }) }).toString(), '{"X":1}');
});

class Example {
  static commands = {
    Send: { method: "send", input: { type: "object" }, output: "string", description: "Sends it" },
    Ping: { method: "ping" },
  };
  constructor() {
    sw.expect("Url", { description: "Where to send" });
    sw.expect("Retries", { default: 3, type: "number" });
    sw.expect("Key", { secret: true, required: false });
  }
  send() { return "sent"; }
  ping() {}
}

test("describes settings, commands and lifecycle", () => {
  const d = sw.describe(Example);
  assert.strictEqual(d.sdkLanguage, "node");
  assert.strictEqual(d.lifecycle, "classic");
  assert.deepStrictEqual(d.protocol, { min: 2, max: 2 });
  const s = Object.fromEntries(d.settings.map((x) => [x.name, x]));
  assert.strictEqual(s.Url.required, true);
  assert.strictEqual(s.Retries.required, false);
  assert.strictEqual(s.Retries.default, "3");
  assert.strictEqual(s.Key.secret, true);
  assert.strictEqual(s.Key.required, false);
  const c = Object.fromEntries(d.commands.map((x) => [x.name, x]));
  assert.strictEqual(c.Send.description, "Sends it");
  assert.strictEqual(c.Send.returnsValue, true);
  assert.strictEqual(c.Ping.returnsValue, false);
  assert.strictEqual(c.Ping.inputSchema, null);
});

test("an adapter with a start method is resident, and commands are inherited", () => {
  class Listener extends Example {
    static commands = { Extra: { method: "send", output: "string" } };
    start() {}
  }
  assert.strictEqual(sw.describe(Listener).lifecycle, "resident");
  assert.deepStrictEqual(Object.keys(commandsOf(Listener)).sort(), ["Extra", "Ping", "Send"]);
});

test("an adapter that cannot be built is still described, with a warning", () => {
  class Broken { constructor() { throw new Error("needs settings"); } }
  assert.strictEqual(sw.describe(Broken).warnings.length, 1);
});

test("values come from startup, then the default, then the fallback", () => {
  sw.expect("Mode", { default: "fast" });
  assert.strictEqual(sw.valueOf("Mode"), "fast");
  assert.strictEqual(sw.valueOf("Missing"), undefined);
  assert.strictEqual(sw.valueOf("Missing", "x"), "x");
});
