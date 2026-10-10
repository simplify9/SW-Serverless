"""How command arguments and results cross the wire, and the JSON Schema that describes them.

The encoding is the protocol's, the same in every language: a string is its raw UTF-8 text, bytes
are passed as they are, nothing is an empty payload, and anything else is JSON. A class can take
charge of its own JSON with ``to_wire``/``from_wire`` and describe it with ``__sw_schema__`` — the
types of a contract package do, to keep the property names the contract fixes.
"""

import base64
import dataclasses
import inspect
import json
import typing

_EMPTY = inspect.Parameter.empty


def _origin(tp):
    return typing.get_origin(tp) or tp


def _strip_optional(tp):
    if typing.get_origin(tp) is typing.Union:
        args = [a for a in typing.get_args(tp) if a is not type(None)]
        if len(args) == 1:
            return args[0]
    return tp


def decode(tp, payload):
    """The argument a command receives, from the payload it was sent."""
    tp = _strip_optional(tp)
    if tp in (_EMPTY, typing.Any, None):
        if not payload:
            return None
        text = payload.decode("utf-8")
        try:
            return json.loads(text)
        except ValueError:
            return text
    if tp is bytes:
        return bytes(payload)
    text = payload.decode("utf-8")
    if tp is str:
        return text
    if not text:
        return None
    value = json.loads(text)
    return from_json(tp, value)


def from_json(tp, value):
    tp = _strip_optional(tp)
    if value is None:
        return None
    if hasattr(tp, "from_wire"):
        return tp.from_wire(value)
    if dataclasses.is_dataclass(tp):
        hints = typing.get_type_hints(tp)
        names = {f.name for f in dataclasses.fields(tp)}
        return tp(**{k: from_json(hints.get(k, typing.Any), v) for k, v in value.items() if k in names})
    origin = _origin(tp)
    args = typing.get_args(tp)
    if origin in (list, tuple, set) and args:
        return origin(from_json(args[0], v) for v in value)
    if origin is dict and len(args) == 2:
        return {k: from_json(args[1], v) for k, v in value.items()}
    if tp is bytes and isinstance(value, str):
        return base64.b64decode(value)
    return value


def encode(value):
    """The payload a command's result becomes."""
    if value is None:
        return b""
    if isinstance(value, (bytes, bytearray, memoryview)):
        return bytes(value)
    if isinstance(value, str):
        return value.encode("utf-8")
    return json.dumps(to_json(value), separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def to_json(value):
    if hasattr(value, "to_wire"):
        return value.to_wire()
    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        return {f.name: to_json(getattr(value, f.name)) for f in dataclasses.fields(value)}
    if isinstance(value, dict):
        return {str(k): to_json(v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set, frozenset)):
        return [to_json(v) for v in value]
    if isinstance(value, (bytes, bytearray)):
        return base64.b64encode(bytes(value)).decode("ascii")
    return value


def schema(tp):
    """A JSON Schema for a command's argument or result type; None when it has none."""
    tp = _strip_optional(tp)
    if tp in (_EMPTY, None, type(None)):
        return None
    if hasattr(tp, "__sw_schema__"):
        return tp.__sw_schema__()
    simple = {str: {"type": "string"}, int: {"type": "integer"}, float: {"type": "number"},
              bool: {"type": "boolean"}, bytes: {"type": "string", "contentEncoding": "binary"}}
    if tp in simple:
        return dict(simple[tp])
    if dataclasses.is_dataclass(tp):
        hints = typing.get_type_hints(tp)
        properties = {f.name: schema(hints.get(f.name, typing.Any)) or {} for f in dataclasses.fields(tp)}
        required = [f.name for f in dataclasses.fields(tp)
                    if f.default is dataclasses.MISSING and f.default_factory is dataclasses.MISSING]
        result = {"type": "object", "properties": properties}
        if required:
            result["required"] = required
        return result
    origin = _origin(tp)
    args = typing.get_args(tp)
    if origin in (list, tuple, set):
        return {"type": "array", "items": (schema(args[0]) or {}) if args else {}}
    if origin is dict:
        return {"type": "object", "additionalProperties": (schema(args[1]) or {}) if len(args) == 2 else {}}
    return {}
