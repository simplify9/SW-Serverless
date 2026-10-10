"""One gRPC bidirectional stream over HTTP/2 cleartext (h2c, prior knowledge), on asyncio.

The host serves gRPC on a Unix domain socket and the adapter opens exactly one call on it, Attach,
for its whole life. That is all this implements: one client stream, the frames it needs, and flow
control in both directions — the part that matters, since a result can be 64 MB and the default
window is 64 KB.
"""

import asyncio
import struct

from . import _hpack

PREFACE = b"PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"

DATA, HEADERS, PRIORITY, RST_STREAM, SETTINGS, PUSH_PROMISE, PING, GOAWAY, WINDOW_UPDATE, CONTINUATION = range(10)
END_STREAM, ACK, END_HEADERS, PADDED, PRIORITY_FLAG = 0x1, 0x1, 0x4, 0x8, 0x20

SETTINGS_HEADER_TABLE_SIZE, SETTINGS_INITIAL_WINDOW_SIZE, SETTINGS_MAX_FRAME_SIZE = 0x1, 0x4, 0x5

STREAM_ID = 1
DEFAULT_WINDOW = 65535
# What the adapter lets the host send before it acknowledges: large, so a big payload is not
# trickled through 64 KB at a time.
RECEIVE_WINDOW = 16 * 1024 * 1024
MAX_MESSAGE = 64 * 1024 * 1024


class GrpcError(Exception):
    def __init__(self, status, message):
        super().__init__(f"gRPC status {status}: {message}")
        self.status = status


class GrpcStream:
    """Attach: write messages, read messages, and know when the host has ended the call."""

    def __init__(self, reader, writer, path):
        self._reader = reader
        self._writer = writer
        self._path = path
        self._decoder = _hpack.Decoder()
        # Messages go out whole and in order, under the message lock, which may wait for window.
        # Every socket write takes only the write lock, briefly, so the read loop can still
        # acknowledge frames — and so receive the WINDOW_UPDATE that wait is for.
        self._message_lock = asyncio.Lock()
        self._write_lock = asyncio.Lock()
        self._window_changed = asyncio.Condition()
        self._conn_send_window = DEFAULT_WINDOW
        self._stream_send_window = DEFAULT_WINDOW
        self._peer_initial_window = DEFAULT_WINDOW
        self._peer_max_frame = 16384
        self._inbound = asyncio.Queue()
        self._buffer = bytearray()
        self._header_block = bytearray()
        self._header_stream = 0
        self._header_end_stream = False
        self._closed = False
        self._read_task = None

    @classmethod
    async def open(cls, socket_path, path):
        reader, writer = await asyncio.open_unix_connection(socket_path, limit=2 ** 20)
        stream = cls(reader, writer, path)
        await stream._start()
        return stream

    # ------------------------------------------------------------------ framing

    def _frame(self, frame_type, flags, stream_id, payload=b""):
        return struct.pack(">I", len(payload))[1:] + bytes([frame_type, flags]) + struct.pack(">I", stream_id) + payload

    async def _write(self, data):
        async with self._write_lock:
            self._writer.write(data)
            await self._writer.drain()

    async def _start(self):
        settings = struct.pack(">HI", SETTINGS_INITIAL_WINDOW_SIZE, RECEIVE_WINDOW)
        # Raise the connection-level window too; it starts at 64 KB whatever SETTINGS says.
        grow = struct.pack(">I", RECEIVE_WINDOW - DEFAULT_WINDOW)
        headers = _hpack.encode([
            (":method", "POST"), (":scheme", "http"), (":path", self._path), (":authority", "localhost"),
            ("content-type", "application/grpc"), ("te", "trailers"),
            ("user-agent", "sw-serverless-python"),
        ])
        await self._write(
            PREFACE
            + self._frame(SETTINGS, 0, 0, settings)
            + self._frame(WINDOW_UPDATE, 0, 0, grow)
            + self._frame(HEADERS, END_HEADERS, STREAM_ID, headers))
        self._read_task = asyncio.get_running_loop().create_task(self._read_loop())

    # ------------------------------------------------------------------ sending

    async def send(self, message):
        if len(message) > MAX_MESSAGE:
            raise ValueError(f"a message of {len(message)} bytes is more than the {MAX_MESSAGE} the host accepts")
        data = b"\x00" + struct.pack(">I", len(message)) + message
        async with self._message_lock:
            pos = 0
            while pos < len(data):
                async with self._window_changed:
                    await self._window_changed.wait_for(
                        lambda: self._closed or (self._conn_send_window > 0 and self._stream_send_window > 0))
                    if self._closed:
                        raise ConnectionError("the host closed the stream")
                    size = min(len(data) - pos, self._conn_send_window, self._stream_send_window, self._peer_max_frame)
                    self._conn_send_window -= size
                    self._stream_send_window -= size
                await self._write(self._frame(DATA, 0, STREAM_ID, data[pos:pos + size]))
                pos += size

    async def close_send(self):
        async with self._message_lock:
            if not self._closed:
                try:
                    await self._write(self._frame(DATA, END_STREAM, STREAM_ID))
                except (ConnectionError, OSError):
                    pass

    # ------------------------------------------------------------------ receiving

    async def receive(self):
        """The next message, or None once the host has ended the call."""
        item = await self._inbound.get()
        if isinstance(item, Exception):
            raise item
        return item

    async def _read_loop(self):
        try:
            while True:
                head = await self._reader.readexactly(9)
                length = int.from_bytes(head[:3], "big")
                frame_type, flags = head[3], head[4]
                stream_id = int.from_bytes(head[5:9], "big") & 0x7FFFFFFF
                payload = await self._reader.readexactly(length) if length else b""
                if await self._on_frame(frame_type, flags, stream_id, payload):
                    break
            await self._inbound.put(None)
        except (asyncio.IncompleteReadError, ConnectionError, OSError):
            await self._inbound.put(None)
        except Exception as ex:  # a protocol error: end the call rather than hang
            await self._inbound.put(ex)
        finally:
            await self._mark_closed()

    async def _mark_closed(self):
        self._closed = True
        async with self._window_changed:
            self._window_changed.notify_all()

    async def _on_frame(self, frame_type, flags, stream_id, payload):
        """True when the call is over."""
        if frame_type == SETTINGS:
            if not flags & ACK:
                await self._apply_settings(payload)
                await self._write(self._frame(SETTINGS, ACK, 0))
        elif frame_type == PING:
            if not flags & ACK:
                await self._write(self._frame(PING, ACK, 0, payload))
        elif frame_type == WINDOW_UPDATE:
            increment = int.from_bytes(payload[:4], "big") & 0x7FFFFFFF
            async with self._window_changed:
                if stream_id == 0:
                    self._conn_send_window += increment
                elif stream_id == STREAM_ID:
                    self._stream_send_window += increment
                self._window_changed.notify_all()
        elif frame_type in (HEADERS, CONTINUATION):
            if frame_type == HEADERS:
                payload = self._strip(flags, payload, headers=True)
                self._header_block = bytearray(payload)
                self._header_stream = stream_id
                self._header_end_stream = bool(flags & END_STREAM)
            else:
                self._header_block += payload
            if flags & END_HEADERS:
                headers = self._decoder.decode(bytes(self._header_block))
                if self._header_stream == STREAM_ID:
                    self._check_headers(headers)
                    if self._header_end_stream:
                        return True
        elif frame_type == DATA:
            data = self._strip(flags, payload)
            if length := len(payload):
                # Hand the window straight back: messages are consumed as they are parsed.
                increment = struct.pack(">I", length)
                await self._write(self._frame(WINDOW_UPDATE, 0, 0, increment)
                                  + self._frame(WINDOW_UPDATE, 0, STREAM_ID, increment))
            if stream_id == STREAM_ID:
                self._buffer += data
                await self._drain_messages()
                if flags & END_STREAM:
                    return True
        elif frame_type == RST_STREAM and stream_id == STREAM_ID:
            code = int.from_bytes(payload[:4], "big")
            raise GrpcError(code, "the host reset the stream")
        elif frame_type == GOAWAY:
            return True
        return False

    async def _apply_settings(self, payload):
        for i in range(0, len(payload) - 5, 6):
            ident, value = struct.unpack(">HI", payload[i:i + 6])
            if ident == SETTINGS_INITIAL_WINDOW_SIZE:
                async with self._window_changed:
                    self._stream_send_window += value - self._peer_initial_window
                    self._peer_initial_window = value
                    self._window_changed.notify_all()
            elif ident == SETTINGS_MAX_FRAME_SIZE:
                self._peer_max_frame = value

    @staticmethod
    def _strip(flags, payload, headers=False):
        pad = 0
        if flags & PADDED:
            pad = payload[0]
            payload = payload[1:]
        if headers and flags & PRIORITY_FLAG:
            payload = payload[5:]
        return payload[:len(payload) - pad] if pad else payload

    def _check_headers(self, headers):
        values = dict(headers)
        status = values.get(":status")
        if status is not None and status != "200":
            raise GrpcError(-1, f"HTTP status {status}")
        grpc_status = values.get("grpc-status")
        if grpc_status is not None and grpc_status != "0":
            raise GrpcError(int(grpc_status), values.get("grpc-message", ""))

    async def _drain_messages(self):
        while len(self._buffer) >= 5:
            if self._buffer[0] != 0:
                raise GrpcError(-1, "compressed messages are not supported")
            length = int.from_bytes(self._buffer[1:5], "big")
            if len(self._buffer) < 5 + length:
                return
            message = bytes(self._buffer[5:5 + length])
            del self._buffer[:5 + length]
            await self._inbound.put(message)

    async def aclose(self):
        await self.close_send()
        try:
            self._writer.close()
            await self._writer.wait_closed()
        except (ConnectionError, OSError):
            pass
        if self._read_task:
            self._read_task.cancel()
