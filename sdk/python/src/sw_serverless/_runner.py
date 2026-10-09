"""Running an adapter: the stdin handshake, the gRPC stream to the host, and every frame on it.

The process is started by the host, which writes one JSON line on stdin — where to dial and a
one-time token — and keeps stdin open: when it closes, the host is gone and the adapter exits.
"""

import asyncio
import contextvars
import inspect
import itertools
import json
import logging
import os
import sys
import threading
import time
import traceback

from . import _adapter, _types, _wire
from ._http2 import GrpcStream

SDK_VERSION = "10.2.0"
SDK_LANGUAGE = "python"
PROTOCOL = 2
ATTACH = "/sw.serverless.v1.AdapterHost/Attach"
DESCRIBE_FLAG = "--describe"
DESCRIBE_VERSION = 1

# ILogger levels, which the host's log pipeline reads: Trace 0 .. Critical 5.
_LEVELS = ((logging.CRITICAL, 5), (logging.ERROR, 4), (logging.WARNING, 3), (logging.INFO, 2), (logging.DEBUG, 1))

_context = contextvars.ContextVar("sw_context", default=None)


class AdapterError(Exception):
    """An error with a type of the adapter's choosing, which the host records as given."""

    def __init__(self, message, *, type=None, detail=None):
        super().__init__(message)
        self.type = type
        self.detail = detail


class Context:
    """What a call can reach beyond its argument: its session, the host's state and events, logs."""

    def __init__(self, runner, session_id=None, command=None, cancelled=None):
        self._runner = runner
        self.session_id = session_id
        self.command = command
        self.cancelled = cancelled or asyncio.Event()

    @property
    def adapter_id(self):
        return self._runner.handshake.get("adapterId", "")

    @property
    def instance_key(self):
        return self._runner.handshake.get("instanceKey", "")

    @property
    def stopping(self):
        """Set once the host has asked the adapter to stop."""
        return self._runner.stopping

    def value_of(self, name, default=None):
        return _adapter.value_of(name, default)

    async def publish(self, payload, *, dedupe_key="", content_type="", headers=None, endpoint=""):
        """Hands an event to the host and waits until it is persisted. Acknowledge the source only
        after this returns: that is what makes delivery at-least-once. Returns the host's reference."""
        ack = await self._runner.request("event", {
            "payload": _types.encode(payload), "dedupe_key": dedupe_key, "content_type": content_type,
            "headers": headers or {}, "endpoint": endpoint}, "event_ack")
        if not ack.get("accepted"):
            error = ack.get("error") or {}
            raise AdapterError(error.get("message") or "the host did not accept the event", type=error.get("type"))
        return ack.get("reference", "")

    async def get_state(self, name):
        result = await self._state(0, name)
        return result.get("value", "") if result.get("found") else None

    async def set_state(self, name, value):
        await self._state(1, name, value)

    async def delete_state(self, name):
        await self._state(2, name)

    async def _state(self, op, name, value=""):
        result = await self._runner.request("state", {"op": op, "name": name, "value": value}, "state_result")
        if result.get("error"):
            raise AdapterError(result["error"].get("message", "state request failed"), type=result["error"].get("type"))
        return result

    def metric(self, name, value, tags=None):
        self._runner.telemetry("metric", {"name": name, "value": float(value), "tags": tags or {}})


def context():
    """The current call's :class:`Context`, or the adapter's own outside a call."""
    current = _context.get()
    if current is None:
        raise RuntimeError("there is no adapter context outside a running adapter")
    return current


class _HostLogHandler(logging.Handler):
    """Python logging, forwarded to the host as log frames once the stream is open."""

    def __init__(self, runner):
        super().__init__()
        self._runner = runner

    def emit(self, record):
        level = next((sw for py, sw in _LEVELS if record.levelno >= py), 0)
        if level < self._runner.minimum_level:
            return
        try:
            message = record.getMessage()
        except Exception:
            message = str(record.msg)
        exception = "".join(traceback.format_exception(*record.exc_info)) if record.exc_info else ""
        self._runner.telemetry("log", {
            "level": level, "message": message, "exception": exception,
            "properties": {"logger": record.name}, "timestamp_unix_ms": int(record.created * 1000)})


class Runner:
    def __init__(self, adapter):
        self.adapter = adapter
        self.cls = type(adapter)
        self.commands = _adapter.commands_of(adapter)
        self.handshake = {}
        self.stream = None
        self.loop = None
        self.stopping = None
        self.minimum_level = 1
        self._ids = itertools.count(1)
        self._pending = {}
        self._running = {}
        self._telemetry = None
        self._ready = None
        self._starting = None

    # ------------------------------------------------------------------ description

    def hello(self):
        return {
            "token": self.handshake.get("token") or "",
            "adapter_id": self.handshake.get("adapterId") or "",
            "instance_key": self.handshake.get("instanceKey") or "",
            "protocol_version": PROTOCOL,
            "sdk_version": SDK_VERSION,
            "sdk_language": SDK_LANGUAGE,
            "capabilities": self.capabilities(),
            "commands": [self._command_info(c) for c in self.commands.values()],
            "settings": [{"name": s.name, "description": s.description or "", "required": s.required,
                          "secret": s.secret, "default_value": s.default or "", "type": s.type}
                         for s in _adapter.declared_settings()],
            "kinds": _adapter.kinds_of(self.cls),
            "contracts": _adapter.contracts_of(self.cls),
        }

    @staticmethod
    def _command_info(command):
        info = command.info()
        return {"name": info["name"], "parameter_type": info["parameter_type"],
                "returns_value": info["returns_value"], "description": info["description"] or "",
                "input_schema": json.dumps(info["input_schema"]) if info["input_schema"] is not None else "",
                "output_schema": json.dumps(info["output_schema"]) if info["output_schema"] is not None else ""}

    def capabilities(self):
        caps = []
        if _adapter.is_resident(self.cls):
            caps.append("resident")
        if callable(getattr(self.adapter, "reset", None)):
            caps.append("resettable")
        caps.append("cancel")
        caps.extend("command:" + name for name in self.commands)
        return caps

    # ------------------------------------------------------------------ running

    async def run(self, handshake_line):
        self.handshake = json.loads(handshake_line)
        if int(self.handshake.get("protocol") or 2) < PROTOCOL:
            raise RuntimeError(f"the host speaks protocol {self.handshake.get('protocol')}; this SDK speaks {PROTOCOL}")
        socket_path = self.handshake.get("socket")
        if not socket_path:
            raise RuntimeError("the handshake names no socket; this SDK runs on Linux and macOS hosts")

        self.loop = asyncio.get_running_loop()
        self.stopping = asyncio.Event()
        self._ready = asyncio.Event()
        self._telemetry = asyncio.Queue(maxsize=10000)
        _context.set(Context(self))

        self.stream = await GrpcStream.open(socket_path, ATTACH)
        await self.send({"hello": self.hello()})

        handler = _HostLogHandler(self)
        logging.getLogger().addHandler(handler)
        if logging.getLogger().level > logging.INFO or logging.getLogger().level == logging.NOTSET:
            logging.getLogger().setLevel(logging.INFO)

        watcher = threading.Thread(target=self._watch_parent, daemon=True)
        watcher.start()
        telemetry = self.loop.create_task(self._pump_telemetry())
        try:
            await self._read_loop()
        finally:
            self.stopping.set()
            logging.getLogger().removeHandler(handler)
            telemetry.cancel()
            await self.stream.aclose()

    def _watch_parent(self):
        # Stdin stays open while the host lives; end of file means it's gone.
        try:
            while sys.stdin.readline():
                pass
        except Exception:
            pass
        if self.loop and not self.loop.is_closed():
            self.loop.call_soon_threadsafe(self.stopping.set)

    async def _read_loop(self):
        stop = self.loop.create_task(self.stopping.wait())
        try:
            while True:
                receive = self.loop.create_task(self.stream.receive())
                done, _ = await asyncio.wait({receive, stop}, return_when=asyncio.FIRST_COMPLETED)
                if receive not in done:
                    receive.cancel()
                    return
                data = receive.result()
                if data is None:
                    return
                frame = _wire.decode("HostFrame", data)
                if await self._on_frame(frame):
                    return
        finally:
            stop.cancel()

    async def _on_frame(self, frame):
        """True when the adapter should stop."""
        frame_id = frame.get("id", 0)
        if "ready" in frame:
            ready = frame["ready"]
            _adapter._startup_values.clear()
            _adapter._startup_values.update(ready.get("startup_values", {}))
            # Started beside the read loop, not inside it: a start that publishes an event or reads its
            # state waits for the host's answer, which only the read loop can take in. Commands wait
            # for _ready; if start fails, the adapter stops.
            self._starting = self.loop.create_task(self._start())
        elif "invoke" in frame:
            self._start_invoke(frame_id, frame["invoke"])
        elif "cancel" in frame:
            running = self._running.get(frame_id)
            if running:
                running[1].set()
                running[0].cancel()
        elif "ping" in frame:
            self.loop.create_task(self._pong(frame_id))
        elif "reset" in frame:
            self.loop.create_task(self._reset(frame_id, frame["reset"].get("session_id", "")))
        elif "set_log_level" in frame:
            self.minimum_level = frame["set_log_level"].get("level", 0)
        elif "shutdown" in frame:
            await self._shutdown(frame["shutdown"])
            return True
        else:
            for answer in ("state_result", "event_ack"):
                if answer in frame:
                    waiter = self._pending.pop(frame_id, None)
                    if waiter and not waiter.done():
                        waiter.set_result(frame[answer])
        return False

    async def _start(self):
        start = getattr(self.adapter, "start", None)
        try:
            if callable(start):
                await _call(start)
            self._ready.set()
        except Exception:
            logging.getLogger("sw_serverless").exception("the adapter failed to start")
            traceback.print_exc()
            self.stopping.set()

    # ------------------------------------------------------------------ commands

    def _start_invoke(self, frame_id, invoke):
        cancelled = asyncio.Event()
        task = self.loop.create_task(self._invoke(frame_id, invoke, cancelled))
        self._running[frame_id] = (task, cancelled)
        task.add_done_callback(lambda _: self._running.pop(frame_id, None))

    async def _invoke(self, frame_id, invoke, cancelled):
        name = invoke.get("command", "")
        try:
            await self._ready.wait()
            command = self.commands.get(name)
            if command is None:
                raise AdapterError(f"the adapter has no command named {name}", type="MissingMethodException")
            _adapter._call_values.set(invoke.get("properties") or {})
            _context.set(Context(self, invoke.get("session_id") or str(frame_id), name, cancelled))
            args = [_types.decode(command.argument_type, invoke.get("payload", b""))] if command.takes_argument else []
            coro = _call(command.method, *args)
            timeout = invoke.get("timeout_seconds") or 0
            result = await (asyncio.wait_for(coro, timeout) if timeout > 0 else coro)
            payload = _types.encode(result) if command.returns_value else b""
            await self.send({"id": frame_id, "invoke_result": {"payload": payload}})
        except asyncio.CancelledError:
            await self._fail(frame_id, "OperationCanceledException", "the call was cancelled", "")
        except Exception as ex:
            await self._fail(frame_id, getattr(ex, "type", None) or _qualified(type(ex)), str(ex),
                             getattr(ex, "detail", None) or traceback.format_exc())

    async def _fail(self, frame_id, error_type, message, detail):
        try:
            await self.send({"id": frame_id, "invoke_result": {"error": {
                "type": error_type, "message": message, "detail": detail}}})
        except Exception:
            pass

    async def _pong(self, frame_id):
        pong = {"connected": True, "state": "Running", "in_flight": len(self._running)}
        status = getattr(self.adapter, "status", None)
        if callable(status):
            try:
                reported = await _call(status) or {}
                pong.update({k: v for k, v in reported.items() if k in
                             ("connected", "state", "in_flight", "last_error", "last_message_unix_ms", "details")})
            except Exception as ex:
                pong.update(connected=False, state="StatusFailed", last_error=str(ex))
        await self.send({"id": frame_id, "pong": pong})

    async def _reset(self, frame_id, session_id):
        result = {}
        reset = getattr(self.adapter, "reset", None)
        if callable(reset):
            try:
                await _call(reset, session_id)
            except Exception as ex:
                result["error"] = {"type": _qualified(type(ex)), "message": str(ex), "detail": traceback.format_exc()}
        await self.send({"id": frame_id, "invoke_result": result})

    async def _shutdown(self, shutdown):
        # Ask it to stop first, let commands already running answer, and only then go: a drain
        # promised those answers.
        deadline = time.monotonic() + (30 if shutdown.get("drain") else 5)
        # A start still running finishes first: a stop that overtook it would leave half of what
        # start set up in place.
        if self._starting is not None and not self._starting.done():
            await asyncio.wait({self._starting}, timeout=max(0.1, deadline - time.monotonic()))
        stop = getattr(self.adapter, "stop", None)
        if callable(stop):
            try:
                await asyncio.wait_for(_call(stop), max(0.1, deadline - time.monotonic()))
            except Exception:
                logging.getLogger("sw_serverless").warning("stop() failed", exc_info=True)
        running = [task for task, _ in list(self._running.values())]
        if running:
            await asyncio.wait(running, timeout=max(0.1, deadline - time.monotonic()))
        await self._flush_telemetry()

    # ------------------------------------------------------------------ outbound

    async def send(self, frame):
        await self.stream.send(_wire.encode("AdapterFrame", frame))

    async def request(self, kind, body, answer):
        """Sends a frame of the adapter's own and waits for the host's answer to it."""
        frame_id = next(self._ids)
        waiter = self.loop.create_future()
        self._pending[frame_id] = waiter
        await self.send({"id": frame_id, kind: body})
        stopping = self.loop.create_task(self.stopping.wait())
        try:
            done, _ = await asyncio.wait({waiter, stopping}, return_when=asyncio.FIRST_COMPLETED)
            if waiter not in done:
                raise AdapterError("the adapter is stopping", type="OperationCanceledException")
            return waiter.result()
        finally:
            stopping.cancel()
            self._pending.pop(frame_id, None)

    def telemetry(self, kind, body):
        """Logs and metrics: queued, and dropped rather than allowed to hold anything up."""
        if self.loop is None or self._telemetry is None:
            return

        def put():
            try:
                self._telemetry.put_nowait({kind: body})
            except asyncio.QueueFull:
                pass

        try:
            if _in_loop(self.loop):
                put()
            else:
                self.loop.call_soon_threadsafe(put)
        except RuntimeError:
            pass  # the loop has closed

    async def _pump_telemetry(self):
        while True:
            frame = await self._telemetry.get()
            try:
                await self.send(frame)
            except Exception:
                return

    async def _flush_telemetry(self):
        while not self._telemetry.empty():
            try:
                await self.send(self._telemetry.get_nowait())
            except Exception:
                return


def _in_loop(loop):
    try:
        return asyncio.get_running_loop() is loop
    except RuntimeError:
        return False


def _qualified(cls):
    module = cls.__module__
    return cls.__qualname__ if module in ("builtins", "__main__") else f"{module}.{cls.__qualname__}"


async def _call(fn, *args):
    """Calls a hook or command, sync or async. Sync ones run on a worker thread, with the call's
    context, so a blocking adapter doesn't stop the host's pings being answered."""
    if inspect.iscoroutinefunction(fn):
        return await fn(*args)
    result = await asyncio.to_thread(fn, *args)
    if inspect.isawaitable(result):
        return await result
    return result


# ---------------------------------------------------------------------------- entry point

def describe(adapter):
    """What ``--describe`` prints: the adapter's settings, commands, kinds and contracts."""
    cls = adapter if isinstance(adapter, type) else type(adapter)
    warnings = []
    instance = adapter
    if isinstance(adapter, type):
        try:
            instance = adapter()
        except Exception as ex:
            warnings.append(f"the adapter could not be built without its settings ({ex}); "
                            "settings it declares when built are missing")
            instance = adapter
    commands = []
    for command in _adapter.commands_of(instance).values():
        info = command.info()
        commands.append({"name": info["name"], "description": info["description"],
                         "inputSchema": info["input_schema"], "outputSchema": info["output_schema"],
                         "returnsValue": info["returns_value"]})
    return {
        "describeVersion": DESCRIBE_VERSION,
        "sdkLanguage": SDK_LANGUAGE,
        "sdkVersion": SDK_VERSION,
        "lifecycle": "resident" if _adapter.is_resident(cls) else "classic",
        "protocol": {"min": PROTOCOL, "max": PROTOCOL},
        "settings": [s.describe() for s in _adapter.declared_settings()],
        "commands": commands,
        "kinds": _adapter.kinds_of(cls),
        "contracts": _adapter.contracts_of(cls),
        "warnings": warnings,
    }


def _check_kinds(adapter):
    check = getattr(adapter, "__sw_check__", None)
    if callable(check):
        check()


def run(adapter):
    """Runs the adapter: describes it for ``--describe``, otherwise serves the host that started it.

    ``adapter`` is an instance, or a class built with no arguments.
    """
    if DESCRIBE_FLAG in sys.argv[1:]:
        sys.stdout.write(json.dumps(describe(adapter), indent=2) + "\n")
        sys.stdout.flush()
        return

    instance = adapter() if isinstance(adapter, type) else adapter
    _check_kinds(instance)

    line = sys.stdin.readline()
    if not line:
        print("stdin closed before the handshake arrived; the host is gone", file=sys.stderr)
        sys.exit(1)
    try:
        asyncio.run(Runner(instance).run(line))
    except KeyboardInterrupt:
        pass
    except Exception:
        traceback.print_exc()
        sys.exit(1)
    # Worker threads running blocking commands must not keep a stopped adapter alive.
    sys.stdout.flush()
    os._exit(0)
