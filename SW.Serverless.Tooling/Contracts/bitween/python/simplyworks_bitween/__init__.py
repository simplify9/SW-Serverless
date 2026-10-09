"""The Bitween adapter contract for Python: the kinds of adapter Bitween runs, and what it passes.

Subclass a kind and implement its methods; the wire names, the encoding and the kind and contract
declarations are taken care of. The contract itself is ``bitween-adapter-contract.v1.json`` in
SW.Bitween.Adapters; this is its Python form, as ``SimplyWorks.Bitween.Adapters`` is its .NET one.

    import simplyworks_serverless as sw
from simplyworks_serverless._runner import _call
    from simplyworks_bitween import ExchangeFile, Handler

    class Orders(Handler):
        def __init__(self):
            sw.expect("Url", description="Where orders go")

        def handle(self, file: ExchangeFile) -> ExchangeFile:
            ...

    if __name__ == "__main__":
        sw.run(Orders)
"""

import hashlib
from dataclasses import dataclass, field

import simplyworks_serverless as sw
from simplyworks_serverless._runner import _call

CONTRACT = "bitween"
CONTRACT_VERSION = 1

__version__ = "10.0.59"


@dataclass
class ExchangeFile:
    """A file as Bitween passes it to and from adapters.

    ``data`` is the content: text, or base64 for binary content. ``bad_data`` marks a bad response,
    a delivery the partner rejected — returned, not raised.
    """

    data: str = ""
    filename: str | None = None
    bad_data: bool = False
    content_type: str | None = None

    @property
    def hash(self):
        """SHA-1 of ``data`` as lower-case hex, as .NET adapters write it."""
        return hashlib.sha1((self.data or "").encode("utf-8")).hexdigest()

    def to_wire(self):
        return {"Filename": self.filename, "Data": self.data or "", "Hash": self.hash,
                "BadData": self.bad_data, "ContentType": self.content_type}

    @classmethod
    def from_wire(cls, value):
        if not isinstance(value, dict):
            raise ValueError("an ExchangeFile is a JSON object")
        # Hash is recomputed, never trusted; unknown properties are ignored.
        return cls(data=value.get("Data") or "", filename=value.get("Filename"),
                   bad_data=bool(value.get("BadData", False)), content_type=value.get("ContentType"))

    @staticmethod
    def __sw_schema__():
        return {
            "title": "ExchangeFile", "type": "object", "required": ["Data"], "additionalProperties": True,
            "properties": {
                "Filename": {"type": ["string", "null"]}, "Data": {"type": "string"},
                "Hash": {"type": ["string", "null"]}, "BadData": {"type": "boolean", "default": False},
                "ContentType": {"type": ["string", "null"]},
            },
        }


@dataclass
class ValidationResult:
    """What a validator found: each failure as a key — often the field it concerns — and a message.
    No failures means valid."""

    validations: list[tuple[str, str]] = field(default_factory=list)

    @property
    def success(self):
        return not self.validations

    def add(self, key, message):
        self.validations.append((key, message))
        return self

    def to_wire(self):
        return {"Success": self.success, "Validations": [{"Key": k, "Value": v} for k, v in self.validations]}

    @classmethod
    def from_wire(cls, value):
        return cls([(v.get("Key", ""), v.get("Value", "")) for v in (value or {}).get("Validations") or []])

    @staticmethod
    def __sw_schema__():
        return {
            "title": "ValidationResult", "type": "object", "required": ["Validations"], "additionalProperties": True,
            "properties": {
                "Success": {"type": "boolean"},
                "Validations": {"type": "array", "items": {
                    "type": "object", "required": ["Key", "Value"],
                    "properties": {"Key": {"type": "string"}, "Value": {"type": "string"}}}},
            },
        }


def _as_file(value):
    if isinstance(value, ExchangeFile):
        return value
    if isinstance(value, str):
        return ExchangeFile(data=value)
    raise TypeError(f"expected an ExchangeFile, got {type(value).__name__}")


class _Kind:
    __sw_contracts__ = {CONTRACT: CONTRACT_VERSION}
    _required = ()

    def __sw_check__(self):
        # A declared kind without its methods fails when the adapter starts, not on first use.
        missing = [m for m in self._required if getattr(getattr(type(self), m), "__sw_abstract__", False)]
        if missing:
            raise TypeError(f"{type(self).__name__} is a Bitween {self.__sw_kinds__[0]} "
                            f"but does not implement {', '.join(missing)}")


def _abstract(fn):
    fn.__sw_abstract__ = True
    return fn


class Handler(_Kind):
    """Delivers a message and returns the partner's response. A rejected delivery is returned with
    ``bad_data=True``, not raised."""

    __sw_kinds__ = ["handler"]
    _required = ("handle",)

    @_abstract
    def handle(self, file: ExchangeFile) -> ExchangeFile:
        raise NotImplementedError

    @sw.command("Handle", description="Delivers a message and returns the partner's response.")
    async def _sw_handle(self, file: ExchangeFile) -> ExchangeFile:
        return _as_file(await _call(self.handle, file))


class Mapper(_Kind):
    """Transforms a message into the shape the next step expects."""

    __sw_kinds__ = ["mapper"]
    _required = ("map",)

    @_abstract
    def map(self, file: ExchangeFile) -> ExchangeFile:
        raise NotImplementedError

    @sw.command("Handle", description="Transforms a message into the shape the next step expects.")
    async def _sw_handle(self, file: ExchangeFile) -> ExchangeFile:
        return _as_file(await _call(self.map, file))


class Validator(_Kind):
    """Checks a message before it is accepted."""

    __sw_kinds__ = ["validator"]
    _required = ("validate",)

    @_abstract
    def validate(self, file: ExchangeFile) -> ValidationResult:
        raise NotImplementedError

    @sw.command("Validate", description="Checks a message before it is accepted.")
    async def _sw_validate(self, file: ExchangeFile) -> ValidationResult:
        result = await _call(self.validate, file)
        if result is None:
            return ValidationResult()
        if isinstance(result, ValidationResult):
            return result
        # A list of (key, message) pairs, or a dict of key -> message, reads naturally too.
        return ValidationResult(list(result.items()) if isinstance(result, dict) else [tuple(r) for r in result])


class Receiver(_Kind):
    """Fetches files from a source on a schedule. One session per run: ``initialize``, ``list_files``,
    then for each file ``get_file`` and — once it is safely taken in — ``delete_file``, and finally
    ``finalize``, which is also called after a failure."""

    __sw_kinds__ = ["receiver"]
    _required = ("list_files", "get_file", "delete_file")

    def initialize(self):
        pass

    @_abstract
    def list_files(self) -> list[str]:
        raise NotImplementedError

    @_abstract
    def get_file(self, file_id: str) -> ExchangeFile:
        raise NotImplementedError

    @_abstract
    def delete_file(self, file_id: str) -> None:
        raise NotImplementedError

    def finalize(self):
        pass

    @sw.command("Initialize", description="Starts a run.")
    async def _sw_initialize(self) -> None:
        await _call(self.initialize)

    @sw.command("ListFiles", description="The ids of the files waiting.")
    async def _sw_list_files(self) -> list[str]:
        return [str(f) for f in (await _call(self.list_files) or [])]

    @sw.command("GetFile", description="One file, by an id ListFiles gave.")
    async def _sw_get_file(self, file_id: str) -> ExchangeFile:
        return _as_file(await _call(self.get_file, file_id))

    @sw.command("DeleteFile", description="Removes a file from the source once it is safely taken in.")
    async def _sw_delete_file(self, file_id: str) -> None:
        await _call(self.delete_file, file_id)

    @sw.command("Finalize", description="Ends a run, after a failure too.")
    async def _sw_finalize(self) -> None:
        await _call(self.finalize)


__all__ = ["CONTRACT", "CONTRACT_VERSION", "ExchangeFile", "Handler", "Mapper", "Receiver", "ValidationResult",
           "Validator"]
