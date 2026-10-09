"""HPACK (RFC 7541): enough to talk to the host.

The adapter sends one set of request headers, encoded as literals without indexing, which every
decoder accepts. What the host sends back is decoded in full: static and dynamic tables, size
updates and Huffman-coded strings, since a server is free to use all of them.
"""

from ._huffman import CODES

STATIC_TABLE = (
    (":authority", ""), (":method", "GET"), (":method", "POST"), (":path", "/"),
    (":path", "/index.html"), (":scheme", "http"), (":scheme", "https"), (":status", "200"),
    (":status", "204"), (":status", "206"), (":status", "304"), (":status", "400"),
    (":status", "404"), (":status", "500"), ("accept-charset", ""), ("accept-encoding", "gzip, deflate"),
    ("accept-language", ""), ("accept-ranges", ""), ("accept", ""), ("access-control-allow-origin", ""),
    ("age", ""), ("allow", ""), ("authorization", ""), ("cache-control", ""),
    ("content-disposition", ""), ("content-encoding", ""), ("content-language", ""), ("content-length", ""),
    ("content-location", ""), ("content-range", ""), ("content-type", ""), ("cookie", ""),
    ("date", ""), ("etag", ""), ("expect", ""), ("expires", ""),
    ("from", ""), ("host", ""), ("if-match", ""), ("if-modified-since", ""),
    ("if-none-match", ""), ("if-range", ""), ("if-unmodified-since", ""), ("last-modified", ""),
    ("link", ""), ("location", ""), ("max-forwards", ""), ("proxy-authenticate", ""),
    ("proxy-authorization", ""), ("range", ""), ("referer", ""), ("refresh", ""),
    ("retry-after", ""), ("server", ""), ("set-cookie", ""), ("strict-transport-security", ""),
    ("transfer-encoding", ""), ("user-agent", ""), ("vary", ""), ("via", ""),
    ("www-authenticate", ""),
)


class HpackError(Exception):
    pass


def _huffman_tree():
    # A binary trie over the codes: each node is [child0, child1, symbol].
    root = [None, None, None]
    for symbol, (code, length) in enumerate(CODES):
        node = root
        for bit in range(length - 1, -1, -1):
            b = (code >> bit) & 1
            if node[b] is None:
                node[b] = [None, None, None]
            node = node[b]
        node[2] = symbol
    return root


_TREE = _huffman_tree()


def huffman_decode(data):
    out = bytearray()
    node = _TREE
    depth = 0
    for byte in data:
        for bit in range(7, -1, -1):
            node = node[(byte >> bit) & 1]
            depth += 1
            if node is None:
                raise HpackError("invalid Huffman code")
            if node[2] is not None:
                if node[2] == 256:
                    raise HpackError("EOS symbol in a Huffman string")
                out.append(node[2])
                node = _TREE
                depth = 0
    # Padding is the most significant bits of EOS (all ones), and shorter than a byte.
    if depth > 7:
        raise HpackError("Huffman padding longer than 7 bits")
    return bytes(out)


def _decode_int(data, pos, prefix_bits):
    mask = (1 << prefix_bits) - 1
    value = data[pos] & mask
    pos += 1
    if value < mask:
        return value, pos
    shift = 0
    while True:
        if pos >= len(data):
            raise HpackError("truncated integer")
        byte = data[pos]
        pos += 1
        value += (byte & 0x7F) << shift
        shift += 7
        if not byte & 0x80:
            return value, pos


def _encode_int(value, prefix_bits, first_byte_flags=0):
    mask = (1 << prefix_bits) - 1
    if value < mask:
        return bytes([first_byte_flags | value])
    out = bytearray([first_byte_flags | mask])
    value -= mask
    while value >= 0x80:
        out.append((value & 0x7F) | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


def _decode_string(data, pos):
    if pos >= len(data):
        raise HpackError("truncated string")
    huffman = data[pos] & 0x80
    length, pos = _decode_int(data, pos, 7)
    raw = data[pos:pos + length]
    if len(raw) != length:
        raise HpackError("truncated string")
    pos += length
    return (huffman_decode(raw) if huffman else bytes(raw)).decode("latin-1"), pos


def _encode_string(text):
    raw = text.encode("latin-1")
    return _encode_int(len(raw), 7) + raw


def encode(headers):
    """Headers as literals without indexing: never touch either side's dynamic table."""
    out = bytearray()
    for name, value in headers:
        out += b"\x00" + _encode_string(name) + _encode_string(value)
    return bytes(out)


class Decoder:
    def __init__(self, max_size=4096):
        self.max_size = max_size
        self.size = 0
        self.dynamic = []  # newest first

    @staticmethod
    def _entry_size(name, value):
        return len(name.encode("latin-1")) + len(value.encode("latin-1")) + 32

    def _evict(self):
        while self.size > self.max_size and self.dynamic:
            name, value = self.dynamic.pop()
            self.size -= self._entry_size(name, value)

    def _add(self, name, value):
        self.dynamic.insert(0, (name, value))
        self.size += self._entry_size(name, value)
        self._evict()

    def _lookup(self, index):
        if index <= 0:
            raise HpackError("index 0")
        if index <= len(STATIC_TABLE):
            return STATIC_TABLE[index - 1]
        index -= len(STATIC_TABLE) + 1
        if index >= len(self.dynamic):
            raise HpackError("index past the dynamic table")
        return self.dynamic[index]

    def decode(self, data):
        headers = []
        pos = 0
        while pos < len(data):
            byte = data[pos]
            if byte & 0x80:  # indexed
                index, pos = _decode_int(data, pos, 7)
                headers.append(self._lookup(index))
            elif byte & 0x40:  # literal, incremental indexing
                index, pos = _decode_int(data, pos, 6)
                name = self._lookup(index)[0] if index else None
                if name is None:
                    name, pos = _decode_string(data, pos)
                value, pos = _decode_string(data, pos)
                self._add(name, value)
                headers.append((name, value))
            elif byte & 0x20:  # dynamic table size update
                size, pos = _decode_int(data, pos, 5)
                self.max_size = size
                self._evict()
            else:  # literal without indexing (0000) or never indexed (0001)
                index, pos = _decode_int(data, pos, 4)
                name = self._lookup(index)[0] if index else None
                if name is None:
                    name, pos = _decode_string(data, pos)
                value, pos = _decode_string(data, pos)
                headers.append((name, value))
        return headers
