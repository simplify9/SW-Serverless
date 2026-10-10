"use strict";
/**
 * Write SW-Serverless adapters in JavaScript or TypeScript, on Node 22 or later.
 *
 *   const sw = require("@simplyworks/sw-serverless");
 *
 *   class Greeter {
 *     static commands = {
 *       Greet: { method: "greet", input: "string", output: "string", description: "Greets someone" },
 *     };
 *     constructor() { sw.expect("Greeting", { default: "Hello" }); }
 *     greet(name) { return `${sw.valueOf("Greeting")}, ${name}`; }
 *   }
 *
 *   sw.run(Greeter);
 *
 * `node adapter.js --describe` prints what it is; the host runs it otherwise. An adapter with a
 * `start` method is resident: it runs until stopped, with `stop`, `status` and `reset` hooks.
 *
 * No dependencies: Node's own http2 speaks gRPC's transport, and wire.js the protobuf messages.
 */

const http2 = require("node:http2");
const net = require("node:net");
const readline = require("node:readline");
const { AsyncLocalStorage } = require("node:async_hooks");
const wire = require("./wire");

const SDK_VERSION = "10.2.0";
const SDK_LANGUAGE = "node";
const PROTOCOL = 2;
const ATTACH = "/sw.serverless.v1.AdapterHost/Attach";
const DESCRIBE_FLAG = "--describe";
const SETTING_TYPES = ["text", "multiline", "number", "boolean", "select", "json"];
const MAX_MESSAGE = 64 * 1024 * 1024;

// ILogger levels, which the host's log pipeline reads: Trace 0 .. Critical 5.
const LEVELS = { trace: 0, debug: 1, info: 2, warn: 3, error: 4, critical: 5 };

class AdapterError extends Error {
  /** An error with a type of the adapter's choosing, which the host records as given. */
  constructor(message, { type, detail } = {}) {
    super(message);
    this.name = "AdapterError";
    this.type = type;
    this.detail = detail;
  }
}

// ---------------------------------------------------------------------------- settings

const settings = new Map();
const startupValues = new Map();
const callStorage = new AsyncLocalStorage();

/**
 * Declares a setting the adapter reads. Required unless it has a default or `required: false` says
 * otherwise. A secret is masked wherever a host application shows it. Declaring a name again replaces it.
 */
function expect(name, options = {}) {
  if (!name || typeof name !== "string") throw new TypeError("a setting needs a name");
  const { default: def, required, secret = false, description, type = "text" } = options;
  if (!SETTING_TYPES.includes(type)) throw new TypeError(`setting type must be one of ${SETTING_TYPES.join(", ")}`);
  settings.set(name, {
    name,
    default: def === undefined || def === null ? null : String(def),
    required: required === undefined ? def === undefined || def === null : Boolean(required),
    secret: Boolean(secret),
    description: description ?? null,
    type,
  });
  return name;
}

/**
 * A setting's value for the current call: the call's own properties, then the values the adapter
 * was started with, then the declared default, then `fallback`.
 */
function valueOf(name, fallback = undefined) {
  const call = callStorage.getStore();
  if (call && call.properties && name in call.properties) return call.properties[name];
  if (startupValues.has(name)) return startupValues.get(name);
  const declared = settings.get(name);
  if (declared && declared.default !== null) return declared.default;
  return fallback;
}

const declaredSettings = () => [...settings.values()];

// ---------------------------------------------------------------------------- commands

/**
 * The commands an adapter class declares in `static commands`, merged down its prototype chain:
 * { WireName: { method, input, output, description } }. `input` and `output` are "string",
 * "bytes", "json" or a JSON Schema object (JSON described by it); no `input` means the command
 * takes no argument, no `output` that it returns nothing.
 */
function commandsOf(cls) {
  const chain = [];
  for (let c = cls; c && c !== Function.prototype; c = Object.getPrototypeOf(c)) chain.unshift(c);
  const found = {};
  for (const c of chain) {
    if (Object.prototype.hasOwnProperty.call(c, "commands")) Object.assign(found, c.commands);
  }
  for (const [name, spec] of Object.entries(found)) {
    if (!spec || typeof spec.method !== "string") throw new TypeError(`command ${name} needs a method name`);
  }
  return found;
}

function collect(cls, property) {
  const values = [];
  for (let c = cls; c && c !== Function.prototype; c = Object.getPrototypeOf(c)) {
    if (Object.prototype.hasOwnProperty.call(c, property)) values.unshift(c[property]);
  }
  return values;
}

const kindsOf = (cls) => [...new Set(collect(cls, "kinds").flat())];
const contractsOf = (cls) => Object.assign({}, ...collect(cls, "contracts"));
const isResident = (cls) => typeof cls.prototype.start === "function";

function schemaOf(spec) {
  if (spec === undefined || spec === null) return null;
  if (typeof spec === "object") return spec;
  return { string: { type: "string" }, bytes: { type: "string", contentEncoding: "binary" }, json: {} }[spec] ?? {};
}

function decodeArgument(spec, payload) {
  if (spec === "bytes") return Buffer.from(payload);
  const text = Buffer.from(payload).toString("utf8");
  if (spec === "string") return text;
  if (!text) return null;
  if (spec === undefined || spec === null) {
    try { return JSON.parse(text); } catch { return text; }
  }
  return JSON.parse(text);
}

function encodeResult(value) {
  if (value === undefined || value === null) return Buffer.alloc(0);
  if (Buffer.isBuffer(value) || value instanceof Uint8Array) return Buffer.from(value);
  if (typeof value === "string") return Buffer.from(value, "utf8");
  return Buffer.from(JSON.stringify(typeof value.toWire === "function" ? value.toWire() : value), "utf8");
}

// ---------------------------------------------------------------------------- describe

function describe(Adapter) {
  const warnings = [];
  try {
    new Adapter();
  } catch (e) {
    warnings.push(`the adapter could not be built without its settings (${e.message}); settings it declares when built are missing`);
  }
  const commands = Object.entries(commandsOf(Adapter)).map(([name, spec]) => ({
    name,
    description: spec.description ?? null,
    inputSchema: schemaOf(spec.input),
    outputSchema: schemaOf(spec.output),
    returnsValue: spec.output !== undefined && spec.output !== null,
  }));
  return {
    describeVersion: 1,
    sdkLanguage: SDK_LANGUAGE,
    sdkVersion: SDK_VERSION,
    lifecycle: isResident(Adapter) ? "resident" : "classic",
    protocol: { min: PROTOCOL, max: PROTOCOL },
    settings: declaredSettings(),
    commands,
    kinds: kindsOf(Adapter),
    contracts: contractsOf(Adapter),
    warnings,
  };
}

// ---------------------------------------------------------------------------- the connection

/** One gRPC bidirectional stream, Attach, over the host's Unix socket. */
class Stream {
  constructor(socketPath) {
    this.session = http2.connect("http://localhost", {
      createConnection: () => net.connect(socketPath),
      settings: { initialWindowSize: 16 * 1024 * 1024 },
      maxSessionMemory: 256,
    });
    this.session.on("error", () => {});
    // The connection's own window starts at 64 KB whatever the settings say; widen it once connected.
    this.session.on("connect", () => { try { this.session.setLocalWindowSize(16 * 1024 * 1024); } catch {} });
    this.call = this.session.request({
      ":method": "POST", ":path": ATTACH, "content-type": "application/grpc", te: "trailers",
      "user-agent": "sw-serverless-node",
    });
    this.buffer = Buffer.alloc(0);
    this.waiting = [];
    this.queue = [];
    this.ended = false;
    this.error = null;
    this.call.on("data", (chunk) => this.onData(chunk));
    this.call.on("trailers", (headers) => {
      const status = headers["grpc-status"];
      if (status !== undefined && String(status) !== "0") this.error = new Error(`gRPC status ${status}: ${headers["grpc-message"] ?? ""}`);
    });
    this.call.on("response", (headers) => {
      const status = headers["grpc-status"];
      if (status !== undefined && String(status) !== "0") this.error = new Error(`gRPC status ${status}: ${headers["grpc-message"] ?? ""}`);
    });
    const end = (err) => {
      if (err && !this.error) this.error = err;
      this.ended = true;
      for (const w of this.waiting.splice(0)) w(null);
    };
    this.call.on("end", () => end());
    this.call.on("close", () => end());
    this.call.on("error", (e) => end(e));
  }

  onData(chunk) {
    this.buffer = this.buffer.length ? Buffer.concat([this.buffer, chunk]) : chunk;
    while (this.buffer.length >= 5) {
      if (this.buffer[0] !== 0) { this.error = new Error("compressed messages are not supported"); this.call.close(); return; }
      const length = this.buffer.readUInt32BE(1);
      if (this.buffer.length < 5 + length) return;
      const message = this.buffer.subarray(5, 5 + length);
      this.buffer = this.buffer.subarray(5 + length);
      const waiter = this.waiting.shift();
      if (waiter) waiter(message); else this.queue.push(message);
    }
  }

  /** The next message, or null once the host has ended the call. */
  receive() {
    if (this.queue.length) return Promise.resolve(this.queue.shift());
    if (this.ended) return Promise.resolve(null);
    return new Promise((resolve) => this.waiting.push(resolve));
  }

  send(message) {
    if (message.length > MAX_MESSAGE) throw new Error(`a message of ${message.length} bytes is more than the ${MAX_MESSAGE} the host accepts`);
    const head = Buffer.alloc(5);
    head.writeUInt32BE(message.length, 1);
    return new Promise((resolve, reject) => {
      if (this.ended || this.call.destroyed) return reject(new Error("the host closed the stream"));
      // write's callback runs once the data is handed on, which is the flow control that matters.
      this.call.write(Buffer.concat([head, message]), (err) => (err ? reject(err) : resolve()));
    });
  }

  close() {
    try { this.call.end(); } catch {}
    try { this.session.close(); } catch {}
  }
}

// ---------------------------------------------------------------------------- the runner

class Context {
  constructor(runner, sessionId, command, signal) {
    this._runner = runner;
    this.sessionId = sessionId;
    this.command = command;
    /** Aborted when the host gives up on the call. */
    this.signal = signal;
  }

  get adapterId() { return this._runner.handshake.adapterId ?? ""; }
  get instanceKey() { return this._runner.handshake.instanceKey ?? ""; }
  /** Aborted once the host has asked the adapter to stop. */
  get stopping() { return this._runner.stopping.signal; }

  valueOf(name, fallback) { return valueOf(name, fallback); }

  /**
   * Hands an event to the host and waits until it is persisted. Acknowledge the source only after
   * this resolves: that is what makes delivery at-least-once. Resolves to the host's reference.
   */
  async publish(payload, { dedupeKey = "", contentType = "", headers = {}, endpoint = "" } = {}) {
    const ack = await this._runner.request("event", {
      payload: encodeResult(payload), dedupe_key: dedupeKey, content_type: contentType, headers, endpoint,
    });
    if (!ack.accepted) {
      const error = ack.error ?? {};
      throw new AdapterError(error.message || "the host did not accept the event", { type: error.type });
    }
    return ack.reference ?? "";
  }

  async getState(name) {
    const result = await this._state(0, name);
    return result.found ? result.value ?? "" : null;
  }

  async setState(name, value) { await this._state(1, name, value); }
  async deleteState(name) { await this._state(2, name); }

  async _state(op, name, value = "") {
    const result = await this._runner.request("state", { op, name, value });
    if (result.error) throw new AdapterError(result.error.message || "state request failed", { type: result.error.type });
    return result;
  }

  metric(name, value, tags = {}) {
    this._runner.telemetry({ metric: { name, value: Number(value), tags } });
  }
}

const contextStorage = new AsyncLocalStorage();
let currentRunner = null;

/** The current call's Context, or the adapter's own outside a call. */
function context() {
  const ctx = contextStorage.getStore() ?? currentRunner?.rootContext;
  if (!ctx) throw new Error("there is no adapter context outside a running adapter");
  return ctx;
}

function errorType(e) {
  if (e && e.type) return e.type;
  return (e && e.constructor && e.constructor.name) || "Error";
}

class Runner {
  constructor(adapter) {
    this.adapter = adapter;
    this.cls = adapter.constructor;
    this.commands = commandsOf(this.cls);
    this.handshake = {};
    this.nextId = 1;
    this.pending = new Map();
    this.running = new Map();
    this.stopping = new AbortController();
    this.minimumLevel = 1;
    this.ready = null;
    this.readyResolve = null;
    this.telemetryQueue = [];
  }

  hello() {
    return {
      token: this.handshake.token ?? "",
      adapter_id: this.handshake.adapterId ?? "",
      instance_key: this.handshake.instanceKey ?? "",
      protocol_version: PROTOCOL,
      sdk_version: SDK_VERSION,
      sdk_language: SDK_LANGUAGE,
      capabilities: this.capabilities(),
      commands: Object.entries(this.commands).map(([name, spec]) => ({
        name,
        parameter_type: spec.input === undefined || spec.input === null ? "" : typeof spec.input === "string" ? spec.input : "object",
        returns_value: spec.output !== undefined && spec.output !== null,
        description: spec.description ?? "",
        input_schema: spec.input === undefined || spec.input === null ? "" : JSON.stringify(schemaOf(spec.input)),
        output_schema: spec.output === undefined || spec.output === null ? "" : JSON.stringify(schemaOf(spec.output)),
      })),
      settings: declaredSettings().map((s) => ({
        name: s.name, description: s.description ?? "", required: s.required, secret: s.secret,
        default_value: s.default ?? "", type: s.type,
      })),
      kinds: kindsOf(this.cls),
      contracts: contractsOf(this.cls),
    };
  }

  capabilities() {
    const caps = [];
    if (isResident(this.cls)) caps.push("resident");
    if (typeof this.adapter.reset === "function") caps.push("resettable");
    caps.push("cancel");
    for (const name of Object.keys(this.commands)) caps.push(`command:${name}`);
    return caps;
  }

  async run(line) {
    this.handshake = JSON.parse(line);
    if (Number(this.handshake.protocol ?? 2) < PROTOCOL)
      throw new Error(`the host speaks protocol ${this.handshake.protocol}; this SDK speaks ${PROTOCOL}`);
    if (!this.handshake.socket) throw new Error("the handshake names no socket; this SDK runs on Linux and macOS hosts");

    this.ready = new Promise((resolve) => (this.readyResolve = resolve));
    this.rootContext = new Context(this, null, null, this.stopping.signal);
    currentRunner = this;
    this.stream = new Stream(this.handshake.socket);
    await this.send({ hello: this.hello() });

    try {
      for (;;) {
        const data = await Promise.race([
          this.stream.receive(),
          new Promise((resolve) => this.stopping.signal.addEventListener("abort", () => resolve(null), { once: true })),
        ]);
        if (data === null) break;
        const frame = wire.decode("HostFrame", data);
        if (await this.onFrame(frame)) break;
      }
      if (this.stream.error && !this.stopping.signal.aborted) throw this.stream.error;
    } finally {
      this.stopping.abort();
      await this.flushTelemetry();
      this.stream.close();
    }
  }

  async onFrame(frame) {
    const id = frame.id ?? 0;
    if (frame.ready) {
      startupValues.clear();
      for (const [k, v] of Object.entries(frame.ready.startup_values ?? {})) startupValues.set(k, v);
      // Started beside the read loop, not inside it: a start that publishes an event or reads its
      // state waits for the host's answer, which only the read loop can take in. Commands wait for
      // ready; if start fails, the adapter stops.
      this.starting = Promise.resolve()
        .then(() => (typeof this.adapter.start === "function" ? this.adapter.start() : undefined))
        .then(() => this.readyResolve(), (e) => {
          console.error(e?.stack ?? e);
          process.exitCode = 1;
          this.stopping.abort();
        });
    } else if (frame.invoke) {
      this.startInvoke(id, frame.invoke);
    } else if (frame.cancel) {
      this.running.get(id)?.abort();
    } else if (frame.ping) {
      this.pong(id);
    } else if (frame.reset) {
      this.reset(id, frame.reset.session_id ?? "");
    } else if (frame.set_log_level) {
      this.minimumLevel = frame.set_log_level.level ?? 0;
    } else if (frame.shutdown) {
      await this.shutdown(frame.shutdown);
      return true;
    } else {
      const answer = frame.state_result ?? frame.event_ack;
      if (answer) {
        const waiter = this.pending.get(id);
        if (waiter) { this.pending.delete(id); waiter(answer); }
      }
    }
    return false;
  }

  startInvoke(id, invoke) {
    const controller = new AbortController();
    this.running.set(id, controller);
    const task = this.invoke(id, invoke, controller).finally(() => this.running.delete(id));
    controller.task = task;
  }

  async invoke(id, invoke, controller) {
    const name = invoke.command ?? "";
    let timer;
    try {
      await this.ready;
      const spec = this.commands[name];
      if (!spec) throw new AdapterError(`the adapter has no command named ${name}`, { type: "MissingMethodException" });
      const method = this.adapter[spec.method];
      if (typeof method !== "function") throw new AdapterError(`the adapter has no method ${spec.method} for ${name}`, { type: "MissingMethodException" });

      const takes = spec.input !== undefined && spec.input !== null;
      const args = takes ? [decodeArgument(spec.input, invoke.payload ?? Buffer.alloc(0))] : [];
      const ctx = new Context(this, invoke.session_id || String(id), name, controller.signal);
      const cancelled = new Promise((_, reject) => {
        const fail = () => reject(new AdapterError("the call was cancelled", { type: "OperationCanceledException" }));
        if (controller.signal.aborted) fail();
        controller.signal.addEventListener("abort", fail, { once: true });
      });
      if ((invoke.timeout_seconds ?? 0) > 0) timer = setTimeout(() => controller.abort(), invoke.timeout_seconds * 1000);

      const result = await Promise.race([
        callStorage.run({ properties: invoke.properties ?? {} }, () =>
          contextStorage.run(ctx, () => Promise.resolve().then(() => method.apply(this.adapter, args)))),
        cancelled,
      ]);
      const payload = spec.output !== undefined && spec.output !== null ? encodeResult(result) : Buffer.alloc(0);
      await this.send({ id, invoke_result: { payload } });
    } catch (e) {
      await this.send({ id, invoke_result: { error: { type: errorType(e), message: String(e?.message ?? e), detail: e?.detail ?? e?.stack ?? "" } } }).catch(() => {});
    } finally {
      clearTimeout(timer);
    }
  }

  async pong(id) {
    const pong = { connected: true, state: "Running", in_flight: this.running.size };
    if (typeof this.adapter.status === "function") {
      try {
        const s = (await this.adapter.status()) ?? {};
        if ("connected" in s) pong.connected = Boolean(s.connected);
        if (s.state) pong.state = s.state;
        if (s.inFlight !== undefined) pong.in_flight = s.inFlight;
        if (s.lastError) pong.last_error = s.lastError;
        if (s.lastMessageOn) pong.last_message_unix_ms = new Date(s.lastMessageOn).getTime();
        if (s.details) pong.details = Object.fromEntries(Object.entries(s.details).map(([k, v]) => [k, String(v)]));
      } catch (e) {
        Object.assign(pong, { connected: false, state: "StatusFailed", last_error: String(e?.message ?? e) });
      }
    }
    await this.send({ id, pong }).catch(() => {});
  }

  async reset(id, sessionId) {
    const result = {};
    if (typeof this.adapter.reset === "function") {
      try { await this.adapter.reset(sessionId); }
      catch (e) { result.error = { type: errorType(e), message: String(e?.message ?? e), detail: e?.stack ?? "" }; }
    }
    await this.send({ id, invoke_result: result }).catch(() => {});
  }

  async shutdown(shutdown) {
    // Ask it to stop first, let commands already running answer, and only then go: a drain
    // promised those answers.
    const deadline = Date.now() + (shutdown.drain ? 30000 : 5000);
    const within = (promise) => Promise.race([promise, new Promise((r) => setTimeout(r, Math.max(100, deadline - Date.now())))]);
    // A start still running finishes first: a stop that overtook it would leave half of what start
    // set up in place.
    if (this.starting) await within(this.starting.catch(() => {}));
    if (typeof this.adapter.stop === "function") {
      try { await within(Promise.resolve(this.adapter.stop())); } catch (e) { this.log(3, `stop() failed: ${e?.message ?? e}`); }
    }
    const tasks = [...this.running.values()].map((c) => c.task).filter(Boolean);
    if (tasks.length) await within(Promise.allSettled(tasks));
  }

  send(frame) {
    return this.stream.send(wire.encode("AdapterFrame", frame));
  }

  /** Sends a frame of the adapter's own and waits for the host's answer to it. */
  request(kind, body) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      if (this.stopping.signal.aborted) return reject(new AdapterError("the adapter is stopping", { type: "OperationCanceledException" }));
      this.pending.set(id, resolve);
      this.stopping.signal.addEventListener("abort", () => {
        if (this.pending.delete(id)) reject(new AdapterError("the adapter is stopping", { type: "OperationCanceledException" }));
      }, { once: true });
      this.send({ id, [kind]: body }).catch((e) => { this.pending.delete(id); reject(e); });
    });
  }

  /** Logs and metrics: sent in the background, and dropped rather than allowed to hold anything up. */
  telemetry(frame) {
    if (this.telemetryQueue.length > 10000) return;
    this.telemetryQueue.push(frame);
    if (this.telemetryQueue.length === 1) queueMicrotask(() => this.flushTelemetry());
  }

  async flushTelemetry() {
    while (this.telemetryQueue.length) {
      const frame = this.telemetryQueue.shift();
      try { await this.send(frame); } catch { this.telemetryQueue.length = 0; return; }
    }
  }

  log(level, message, error) {
    if (level < this.minimumLevel) return;
    this.telemetry({ log: { level, message: String(message), exception: error ? String(error.stack ?? error) : "", timestamp_unix_ms: Date.now() } });
  }
}

/** Logs that reach the host: sw.log.info("…"), and debug, warn, error, critical. */
const log = Object.fromEntries(Object.entries(LEVELS).map(([name, level]) => [name, (message, error) => {
  if (currentRunner) currentRunner.log(level, message, error);
  else (level >= 3 ? console.error : console.log)(message, error ?? "");
}]));

function checkKinds(adapter) {
  if (typeof adapter.__swCheck === "function") adapter.__swCheck();
}

/**
 * Runs the adapter: describes it for --describe, otherwise serves the host that started it.
 * `Adapter` is a class built with no arguments.
 */
function run(Adapter) {
  if (process.argv.slice(2).includes(DESCRIBE_FLAG)) {
    process.stdout.write(JSON.stringify(describe(Adapter), null, 2) + "\n");
    return Promise.resolve();
  }

  const adapter = new Adapter();
  checkKinds(adapter);

  const input = readline.createInterface({ input: process.stdin });
  let runner = null;
  return new Promise((resolve) => {
    let first = true;
    input.on("line", (line) => {
      if (!first) return;
      first = false;
      runner = new Runner(adapter);
      runner.run(line).then(
        () => { resolve(); process.exit(0); },
        (e) => { console.error(e?.stack ?? e); process.exit(1); });
    });
    // Stdin stays open while the host lives; end of file means it's gone.
    input.on("close", () => {
      if (first) { console.error("stdin closed before the handshake arrived; the host is gone"); process.exit(1); }
      runner?.stopping.abort();
    });
  });
}

module.exports = {
  AdapterError, Context, SDK_VERSION, context, declaredSettings, describe, expect, log, run, valueOf,
  startupValues: () => Object.fromEntries(startupValues),
  _internal: { commandsOf, decodeArgument, encodeResult, schemaOf, wire },
};
