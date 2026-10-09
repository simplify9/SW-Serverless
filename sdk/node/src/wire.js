"use strict";
/**
 * The protobuf wire format, for the messages in adapter.proto and nothing else.
 *
 * Hand-written rather than generated so the SDK needs no packages: messages are plain objects keyed
 * by the proto's field names, and each schema below mirrors adapter.proto field for field. A field
 * the schema doesn't know is skipped when decoding, as protobuf does, so a newer host stays readable.
 * The same schemas as the Python SDK's _wire.py.
 */

const VARINT = 0, FIXED64 = 1, LENGTH = 2, FIXED32 = 5;

const SCHEMAS = {
  // host -> adapter
  HostFrame: {
    1: ["id", "int"], 2: ["traceparent", "string"],
    3: ["ready", ["message", "Ready"]], 4: ["invoke", ["message", "Invoke"]],
    5: ["ping", ["message", "Empty"]], 6: ["set_log_level", ["message", "SetLogLevel"]],
    7: ["reset", ["message", "Reset"]], 8: ["shutdown", ["message", "Shutdown"]],
    9: ["event_ack", ["message", "EventAck"]], 10: ["state_result", ["message", "StateResult"]],
    11: ["cancel", ["message", "Empty"]],
  },
  Empty: {},
  Ready: { 1: ["max_in_flight", "int"], 2: ["startup_values", "map"], 3: ["adapter_values", "map"] },
  Invoke: {
    1: ["command", "string"], 2: ["payload", "bytes"], 3: ["timeout_seconds", "int"],
    4: ["session_id", "string"], 5: ["properties", "map"],
  },
  SetLogLevel: { 1: ["level", "int"] },
  Reset: { 1: ["session_id", "string"] },
  Shutdown: { 1: ["reason", "string"], 2: ["drain", "bool"] },
  StateResult: { 1: ["found", "bool"], 2: ["value", "string"], 3: ["error", ["message", "Error"]] },
  EventAck: { 1: ["accepted", "bool"], 2: ["reference", "string"], 3: ["error", ["message", "Error"]] },
  // adapter -> host
  AdapterFrame: {
    1: ["id", "int"], 2: ["traceparent", "string"],
    3: ["hello", ["message", "Hello"]], 4: ["invoke_result", ["message", "InvokeResult"]],
    5: ["event", ["message", "Event"]], 6: ["log", ["message", "LogEntry"]],
    7: ["metric", ["message", "Metric"]], 8: ["pong", ["message", "Pong"]],
    9: ["state", ["message", "StateRequest"]],
  },
  Hello: {
    1: ["token", "string"], 2: ["adapter_id", "string"], 3: ["instance_key", "string"],
    4: ["protocol_version", "int"], 5: ["sdk_version", "string"],
    6: ["capabilities", ["repeated", "string"]], 7: ["commands", ["repeated_message", "CommandInfo"]],
    8: ["sdk_language", "string"], 9: ["settings", ["repeated_message", "SettingInfo"]],
    10: ["kinds", ["repeated", "string"]], 11: ["contracts", "map_int"],
  },
  SettingInfo: {
    1: ["name", "string"], 2: ["description", "string"], 3: ["required", "bool"],
    4: ["secret", "bool"], 5: ["default_value", "string"], 6: ["type", "string"],
  },
  CommandInfo: {
    1: ["name", "string"], 2: ["parameter_type", "string"], 3: ["parameter_schema", "string"],
    4: ["returns_value", "bool"], 5: ["description", "string"],
    6: ["input_schema", "string"], 7: ["output_schema", "string"],
  },
  InvokeResult: { 1: ["payload", "bytes"], 2: ["error", ["message", "Error"]] },
  Error: { 1: ["type", "string"], 2: ["message", "string"], 3: ["detail", "string"] },
  Event: {
    1: ["payload", "bytes"], 2: ["dedupe_key", "string"], 3: ["content_type", "string"],
    4: ["headers", "map"], 5: ["endpoint", "string"],
  },
  StateRequest: { 1: ["op", "int"], 2: ["name", "string"], 3: ["value", "string"] },
  LogEntry: {
    1: ["level", "int"], 2: ["message", "string"], 3: ["exception", "string"],
    4: ["properties", "map"], 5: ["timestamp_unix_ms", "int"],
  },
  Metric: { 1: ["name", "string"], 2: ["value", "double"], 3: ["tags", "map"] },
  Pong: {
    1: ["connected", "bool"], 2: ["state", "string"], 3: ["last_message_unix_ms", "int"],
    4: ["in_flight", "int"], 5: ["last_error", "string"], 6: ["details", "map"],
  },
  _MapEntry: { 1: ["key", "string"], 2: ["value", "string"] },
  _MapIntEntry: { 1: ["key", "string"], 2: ["value", "int"] },
};

const BY_NAME = Object.fromEntries(Object.entries(SCHEMAS).map(([msg, fields]) =>
  [msg, Object.fromEntries(Object.entries(fields).map(([n, [name, kind]]) => [name, [Number(n), kind]]))]));

class WireError extends Error {}

// ---------------------------------------------------------------------------- primitives

function varint(value) {
  let v = BigInt(value);
  if (v < 0n) v += 1n << 64n; // int32/int64 negatives are ten-byte two's complement
  const out = [];
  do {
    let byte = Number(v & 0x7fn);
    v >>= 7n;
    if (v) byte |= 0x80;
    out.push(byte);
  } while (v);
  return Buffer.from(out);
}

function readVarint(data, pos) {
  let result = 0n;
  let shift = 0n;
  for (;;) {
    if (pos >= data.length) throw new WireError("truncated varint");
    const byte = data[pos++];
    result |= BigInt(byte & 0x7f) << shift;
    if (!(byte & 0x80)) return [result, pos];
    shift += 7n;
    if (shift > 63n) throw new WireError("varint too long");
  }
}

const key = (number, wireType) => varint((number << 3) | wireType);
const lengthDelimited = (number, payload) => Buffer.concat([key(number, LENGTH), varint(payload.length), payload]);

// ---------------------------------------------------------------------------- encode

function encode(messageName, message) {
  const fields = BY_NAME[messageName];
  const parts = [];
  for (const [name, value] of Object.entries(message)) {
    if (value === undefined || value === null || !(name in fields)) continue;
    const [number, kind] = fields[name];
    parts.push(encodeField(number, kind, value));
  }
  return Buffer.concat(parts);
}

function encodeField(number, kind, value) {
  if (Array.isArray(kind)) {
    const [tag, inner] = kind;
    if (tag === "message") return lengthDelimited(number, encode(inner, value));
    if (tag === "repeated") return Buffer.concat(value.map((item) => encodeField(number, inner, item)));
    if (tag === "repeated_message") return Buffer.concat(value.map((item) => lengthDelimited(number, encode(inner, item))));
    throw new WireError(`unknown kind ${kind}`);
  }
  // proto3: default values are not written
  switch (kind) {
    case "int": return value ? Buffer.concat([key(number, VARINT), varint(Math.trunc(Number(value)))]) : Buffer.alloc(0);
    case "bool": return value ? Buffer.concat([key(number, VARINT), Buffer.from([1])]) : Buffer.alloc(0);
    case "double": {
      if (!value) return Buffer.alloc(0);
      const b = Buffer.alloc(8);
      b.writeDoubleLE(Number(value));
      return Buffer.concat([key(number, FIXED64), b]);
    }
    case "string": return value ? lengthDelimited(number, Buffer.from(String(value), "utf8")) : Buffer.alloc(0);
    case "bytes": return value && value.length ? lengthDelimited(number, Buffer.from(value)) : Buffer.alloc(0);
    case "map":
      return Buffer.concat(Object.entries(value).filter(([, v]) => v !== undefined && v !== null).map(([k, v]) =>
        lengthDelimited(number, Buffer.concat([encodeField(1, "string", k), encodeField(2, "string", v)]))));
    case "map_int":
      return Buffer.concat(Object.entries(value).map(([k, v]) =>
        lengthDelimited(number, Buffer.concat([encodeField(1, "string", k), encodeField(2, "int", v)]))));
    default: throw new WireError(`unknown kind ${kind}`);
  }
}

// ---------------------------------------------------------------------------- decode

function decode(messageName, data) {
  const fields = SCHEMAS[messageName];
  const message = {};
  let pos = 0;
  while (pos < data.length) {
    let k;
    [k, pos] = readVarint(data, pos);
    const number = Number(k >> 3n);
    const wireType = Number(k & 7n);
    let raw;
    if (wireType === VARINT) [raw, pos] = readVarint(data, pos);
    else if (wireType === FIXED64) { raw = data.subarray(pos, pos + 8); pos += 8; }
    else if (wireType === FIXED32) { raw = data.subarray(pos, pos + 4); pos += 4; }
    else if (wireType === LENGTH) {
      let length;
      [length, pos] = readVarint(data, pos);
      length = Number(length);
      raw = data.subarray(pos, pos + length);
      if (raw.length !== length) throw new WireError("truncated field");
      pos += length;
    } else throw new WireError(`unsupported wire type ${wireType}`);

    if (!(number in fields)) continue; // a field from a newer host
    const [name, kind] = fields[number];
    decodeField(message, name, kind, raw);
  }
  return message;
}

function decodeField(message, name, kind, raw) {
  if (Array.isArray(kind)) {
    const [tag, inner] = kind;
    if (tag === "message") message[name] = decode(inner, raw);
    else if (tag === "repeated") (message[name] ??= []).push(scalar(inner, raw));
    else if (tag === "repeated_message") (message[name] ??= []).push(decode(inner, raw));
    return;
  }
  if (kind === "map" || kind === "map_int") {
    const entry = decode(kind === "map" ? "_MapEntry" : "_MapIntEntry", raw);
    (message[name] ??= {})[entry.key ?? ""] = entry.value ?? (kind === "map_int" ? 0 : "");
    return;
  }
  message[name] = scalar(kind, raw);
}

function scalar(kind, raw) {
  switch (kind) {
    case "int": {
      const signed = raw >= 1n << 63n ? raw - (1n << 64n) : raw;
      return Number(signed);
    }
    case "bool": return raw !== 0n;
    case "double": return raw.readDoubleLE(0);
    case "string": return Buffer.from(raw).toString("utf8");
    case "bytes": return Buffer.from(raw);
    default: throw new WireError(`unknown kind ${kind}`);
  }
}

module.exports = { encode, decode, WireError, SCHEMAS, _key: key, VARINT, LENGTH };
