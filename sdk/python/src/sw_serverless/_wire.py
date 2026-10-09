"""The protobuf wire format, for the messages in adapter.proto and nothing else.

Hand-written rather than generated so the SDK needs no packages: messages are dicts keyed by the
proto's field names, and each schema below mirrors adapter.proto field for field. A field the
schema doesn't know is skipped when decoding, as protobuf does, so a newer host stays readable.
"""

import struct

VARINT, FIXED64, LENGTH, FIXED32 = 0, 1, 2, 5


class WireError(Exception):
    pass


# Field kinds: int (int32/int64/enum), bool, string, bytes, double, message, map (string->string),
# map_int (string->int32), and repeated forms written ("repeated", kind) or ("repeated_message", name).
SCHEMAS = {
    # host -> adapter
    "HostFrame": {
        1: ("id", "int"), 2: ("traceparent", "string"),
        3: ("ready", ("message", "Ready")), 4: ("invoke", ("message", "Invoke")),
        5: ("ping", ("message", "Empty")), 6: ("set_log_level", ("message", "SetLogLevel")),
        7: ("reset", ("message", "Reset")), 8: ("shutdown", ("message", "Shutdown")),
        9: ("event_ack", ("message", "EventAck")), 10: ("state_result", ("message", "StateResult")),
        11: ("cancel", ("message", "Empty")),
    },
    "Empty": {},
    "Ready": {1: ("max_in_flight", "int"), 2: ("startup_values", "map"), 3: ("adapter_values", "map")},
    "Invoke": {
        1: ("command", "string"), 2: ("payload", "bytes"), 3: ("timeout_seconds", "int"),
        4: ("session_id", "string"), 5: ("properties", "map"),
    },
    "SetLogLevel": {1: ("level", "int")},
    "Reset": {1: ("session_id", "string")},
    "Shutdown": {1: ("reason", "string"), 2: ("drain", "bool")},
    "StateResult": {1: ("found", "bool"), 2: ("value", "string"), 3: ("error", ("message", "Error"))},
    "EventAck": {1: ("accepted", "bool"), 2: ("reference", "string"), 3: ("error", ("message", "Error"))},
    # adapter -> host
    "AdapterFrame": {
        1: ("id", "int"), 2: ("traceparent", "string"),
        3: ("hello", ("message", "Hello")), 4: ("invoke_result", ("message", "InvokeResult")),
        5: ("event", ("message", "Event")), 6: ("log", ("message", "LogEntry")),
        7: ("metric", ("message", "Metric")), 8: ("pong", ("message", "Pong")),
        9: ("state", ("message", "StateRequest")),
    },
    "Hello": {
        1: ("token", "string"), 2: ("adapter_id", "string"), 3: ("instance_key", "string"),
        4: ("protocol_version", "int"), 5: ("sdk_version", "string"),
        6: ("capabilities", ("repeated", "string")), 7: ("commands", ("repeated_message", "CommandInfo")),
        8: ("sdk_language", "string"), 9: ("settings", ("repeated_message", "SettingInfo")),
        10: ("kinds", ("repeated", "string")), 11: ("contracts", "map_int"),
    },
    "SettingInfo": {
        1: ("name", "string"), 2: ("description", "string"), 3: ("required", "bool"),
        4: ("secret", "bool"), 5: ("default_value", "string"), 6: ("type", "string"),
    },
    "CommandInfo": {
        1: ("name", "string"), 2: ("parameter_type", "string"), 3: ("parameter_schema", "string"),
        4: ("returns_value", "bool"), 5: ("description", "string"),
        6: ("input_schema", "string"), 7: ("output_schema", "string"),
    },
    "InvokeResult": {1: ("payload", "bytes"), 2: ("error", ("message", "Error"))},
    "Error": {1: ("type", "string"), 2: ("message", "string"), 3: ("detail", "string")},
    "Event": {
        1: ("payload", "bytes"), 2: ("dedupe_key", "string"), 3: ("content_type", "string"),
        4: ("headers", "map"), 5: ("endpoint", "string"),
    },
    "StateRequest": {1: ("op", "int"), 2: ("name", "string"), 3: ("value", "string")},
    "LogEntry": {
        1: ("level", "int"), 2: ("message", "string"), 3: ("exception", "string"),
        4: ("properties", "map"), 5: ("timestamp_unix_ms", "int"),
    },
    "Metric": {1: ("name", "string"), 2: ("value", "double"), 3: ("tags", "map")},
    "Pong": {
        1: ("connected", "bool"), 2: ("state", "string"), 3: ("last_message_unix_ms", "int"),
        4: ("in_flight", "int"), 5: ("last_error", "string"), 6: ("details", "map"),
    },
}

_BY_NAME = {msg: {name: (number, kind) for number, (name, kind) in fields.items()} for msg, fields in SCHEMAS.items()}


# ---------------------------------------------------------------------------- primitives

def _varint(value):
    if value < 0:
        value += 1 << 64  # int32/int64 negatives are ten-byte two's complement
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def _read_varint(data, pos):
    result = 0
    shift = 0
    while True:
        if pos >= len(data):
            raise WireError("truncated varint")
        byte = data[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return result, pos
        shift += 7
        if shift > 63:
            raise WireError("varint too long")


def _signed(value):
    return value - (1 << 64) if value >= 1 << 63 else value


def _key(number, wire_type):
    return _varint((number << 3) | wire_type)


def _length_delimited(number, payload):
    return _key(number, LENGTH) + _varint(len(payload)) + payload


# ---------------------------------------------------------------------------- encode

def encode(message_name, message):
    fields = _BY_NAME[message_name]
    out = bytearray()
    for name, value in message.items():
        if value is None or name not in fields:
            continue
        number, kind = fields[name]
        out += _encode_field(number, kind, value)
    return bytes(out)


def _encode_field(number, kind, value):
    if isinstance(kind, tuple):
        tag, inner = kind
        if tag == "message":
            return _length_delimited(number, encode(inner, value))
        if tag == "repeated":
            return b"".join(_encode_field(number, inner, item) for item in value)
        if tag == "repeated_message":
            return b"".join(_length_delimited(number, encode(inner, item)) for item in value)
        raise WireError(f"unknown kind {kind}")
    # proto3: default values are not written
    if kind == "int":
        return _key(number, VARINT) + _varint(int(value)) if value else b""
    if kind == "bool":
        return _key(number, VARINT) + b"\x01" if value else b""
    if kind == "double":
        return _key(number, FIXED64) + struct.pack("<d", float(value)) if value else b""
    if kind == "string":
        return _length_delimited(number, str(value).encode("utf-8")) if value else b""
    if kind == "bytes":
        return _length_delimited(number, bytes(value)) if value else b""
    if kind == "map":
        return b"".join(
            _length_delimited(number, _encode_field(1, "string", k) + _encode_field(2, "string", v))
            for k, v in value.items())
    if kind == "map_int":
        return b"".join(
            _length_delimited(number, _encode_field(1, "string", k) + _encode_field(2, "int", v))
            for k, v in value.items())
    raise WireError(f"unknown kind {kind}")


# ---------------------------------------------------------------------------- decode

def decode(message_name, data):
    fields = SCHEMAS[message_name]
    message = {}
    pos = 0
    data = memoryview(data)
    while pos < len(data):
        key, pos = _read_varint(data, pos)
        number, wire_type = key >> 3, key & 7
        if wire_type == VARINT:
            raw, pos = _read_varint(data, pos)
        elif wire_type == FIXED64:
            raw = bytes(data[pos:pos + 8])
            pos += 8
        elif wire_type == FIXED32:
            raw = bytes(data[pos:pos + 4])
            pos += 4
        elif wire_type == LENGTH:
            length, pos = _read_varint(data, pos)
            raw = bytes(data[pos:pos + length])
            if len(raw) != length:
                raise WireError("truncated field")
            pos += length
        else:
            raise WireError(f"unsupported wire type {wire_type}")

        if number not in fields:
            continue  # a field from a newer host
        name, kind = fields[number]
        _decode_field(message, name, kind, raw)
    return message


def _decode_field(message, name, kind, raw):
    if isinstance(kind, tuple):
        tag, inner = kind
        if tag == "message":
            message[name] = decode(inner, raw)
        elif tag == "repeated":
            message.setdefault(name, []).append(_scalar(inner, raw))
        elif tag == "repeated_message":
            message.setdefault(name, []).append(decode(inner, raw))
        return
    if kind in ("map", "map_int"):
        entry = decode("_MapEntry" if kind == "map" else "_MapIntEntry", raw)
        message.setdefault(name, {})[entry.get("key", "")] = entry.get("value", 0 if kind == "map_int" else "")
        return
    message[name] = _scalar(kind, raw)


def _scalar(kind, raw):
    if kind == "int":
        return _signed(raw)
    if kind == "bool":
        return bool(raw)
    if kind == "double":
        return struct.unpack("<d", raw)[0]
    if kind == "string":
        return raw.decode("utf-8")
    if kind == "bytes":
        return raw
    raise WireError(f"unknown kind {kind}")


SCHEMAS["_MapEntry"] = {1: ("key", "string"), 2: ("value", "string")}
SCHEMAS["_MapIntEntry"] = {1: ("key", "string"), 2: ("value", "int")}
_BY_NAME["_MapEntry"] = {"key": (1, "string"), "value": (2, "string")}
_BY_NAME["_MapIntEntry"] = {"key": (1, "string"), "value": (2, "int")}
