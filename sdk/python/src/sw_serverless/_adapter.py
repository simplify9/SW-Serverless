"""Declaring an adapter: its settings, its commands, its kinds and contracts.

Settings are declared once, in code, with :func:`expect` — the Python form of the .NET SDK's
``Runner.Expect`` — and reach the manifest through ``--describe`` and the host through ``Hello``.
"""

import contextvars
import inspect
import typing

from . import _types

SETTING_TYPES = ("text", "multiline", "number", "boolean", "select", "json")


class Setting:
    def __init__(self, name, default, required, secret, description, type):
        self.name = name
        self.default = default
        self.required = required
        self.secret = secret
        self.description = description
        self.type = type

    def describe(self):
        return {"name": self.name, "description": self.description, "type": self.type,
                "required": self.required, "secret": self.secret, "default": self.default}


_settings = {}


def expect(name, default=None, *, required=None, secret=False, description=None, type="text"):
    """Declares a setting the adapter reads.

    Required unless it has a default or ``required=False`` says otherwise. A secret is masked
    wherever a host application shows it. Declaring the same name again replaces it.
    """
    if not name or not isinstance(name, str):
        raise ValueError("a setting needs a name")
    if type not in SETTING_TYPES:
        raise ValueError(f"setting type must be one of {', '.join(SETTING_TYPES)}")
    if required is None:
        required = default is None
    _settings[name] = Setting(name, None if default is None else str(default), bool(required), bool(secret),
                              description, type)
    return name


def declared_settings():
    return list(_settings.values())


# The values a call sees: this invocation's properties over the process's startup values.
_startup_values = {}
_call_values = contextvars.ContextVar("sw_call_values", default=None)


def value_of(name, default=None):
    """A setting's value for the current call: the call's own properties, then the values the
    adapter was started with, then the declared default, then ``default``."""
    call = _call_values.get()
    if call and name in call:
        return call[name]
    if name in _startup_values:
        return _startup_values[name]
    declared = _settings.get(name)
    if declared is not None and declared.default is not None:
        return declared.default
    return default


def startup_values():
    return dict(_startup_values)


def command(name=None, *, description=None):
    """Marks a method as a command the host can call, under ``name`` or the method's own name."""

    def mark(fn):
        fn.__sw_command__ = {"name": name or fn.__name__, "description": description}
        return fn

    if callable(name):  # used bare: @command
        fn, name = name, None
        return mark(fn)
    return mark


class Command:
    def __init__(self, name, method, description):
        self.name = name
        self.method = method
        self.description = description
        signature = inspect.signature(method)
        parameters = [p for p in signature.parameters.values()
                      if p.name != "self" and p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD)]
        if len(parameters) > 1:
            raise TypeError(f"command {name} takes {len(parameters)} arguments; a command takes at most one")
        try:
            hints = typing.get_type_hints(method)
        except Exception:
            hints = {}
        self.takes_argument = bool(parameters)
        self.argument_type = hints.get(parameters[0].name, _types._EMPTY) if parameters else None
        returns = hints.get("return", signature.return_annotation)
        self.returns_value = returns not in (None, type(None))
        self.result_type = returns

    def info(self):
        input_schema = _types.schema(self.argument_type) if self.takes_argument else None
        output_schema = _types.schema(self.result_type) if self.returns_value else None
        return {"name": self.name, "description": self.description, "takes_argument": self.takes_argument,
                "returns_value": self.returns_value, "input_schema": input_schema, "output_schema": output_schema,
                "parameter_type": _type_name(self.argument_type) if self.takes_argument else ""}


def _type_name(tp):
    if tp in (_types._EMPTY, None):
        return "object"
    return getattr(tp, "__name__", str(tp))


def implements(contract, version, *kinds):
    """Declares, on an adapter class, a contract it implements and the kinds of it it is.

        @sw.implements("orders", 1, "processor")
        class Orders: ...

    A host application's contract package does this for its own base classes."""

    if not contract or not isinstance(version, int):
        raise ValueError("a contract needs a name and an integer version")

    def mark(cls):
        contracts = dict(vars(cls).get("__sw_contracts__", {}))
        contracts[contract] = version
        cls.__sw_contracts__ = contracts
        cls.__sw_kinds__ = list(dict.fromkeys([*vars(cls).get("__sw_kinds__", []), *kinds]))
        return cls

    return mark


def commands_of(adapter):
    """Every command on the adapter's class, by wire name. Kinds' base classes add their own."""
    found = {}
    cls = adapter if isinstance(adapter, type) else type(adapter)
    for klass in reversed(cls.__mro__):
        for attr, value in vars(klass).items():
            marker = getattr(value, "__sw_command__", None)
            if marker:
                found[marker["name"]] = attr
    result = {}
    for wire_name, attr in found.items():
        method = getattr(adapter, attr)
        marker = getattr(getattr(cls, attr), "__sw_command__", None) or {}
        result[wire_name] = Command(wire_name, method, marker.get("description"))
    return result


def kinds_of(cls):
    kinds = []
    for klass in cls.__mro__:
        for kind in getattr(klass, "__sw_kinds__", ()) or ():
            if kind not in kinds:
                kinds.append(kind)
    return kinds


def contracts_of(cls):
    contracts = {}
    for klass in reversed(cls.__mro__):
        contracts.update(getattr(klass, "__sw_contracts__", {}) or {})
    return contracts


def is_resident(cls):
    """Resident when it has a start hook: it runs until stopped rather than for one session."""
    return callable(getattr(cls, "start", None))
