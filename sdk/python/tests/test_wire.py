import unittest

from sw_serverless import _hpack, _wire


class WireTests(unittest.TestCase):
    def test_a_frame_round_trips_with_every_kind_of_field(self):
        frame = {"id": 42, "hello": {
            "token": "t", "protocol_version": 2, "capabilities": ["cancel", "command:Greet"],
            "commands": [{"name": "Greet", "returns_value": True, "input_schema": '{"type":"string"}'}],
            "settings": [{"name": "Url", "required": True, "secret": True}],
            "kinds": ["processor"], "contracts": {"orders": 1}}}
        decoded = _wire.decode("AdapterFrame", _wire.encode("AdapterFrame", frame))
        self.assertEqual(frame, decoded)

    def test_maps_doubles_bytes_and_negative_numbers(self):
        frame = {"id": -7, "metric": {"name": "m", "value": 2.5, "tags": {"a": "b", "c": ""}}}
        self.assertEqual(frame, _wire.decode("AdapterFrame", _wire.encode("AdapterFrame", frame)))
        result = {"invoke_result": {"payload": b"\x00\xff"}}
        self.assertEqual(result, _wire.decode("AdapterFrame", _wire.encode("AdapterFrame", result)))

    def test_an_empty_message_is_present(self):
        # Ping and Cancel carry nothing: present is all they say.
        data = _wire._key(5, _wire.LENGTH) + b"\x00"
        self.assertEqual({"ping": {}}, _wire.decode("HostFrame", data))

    def test_fields_from_a_newer_host_are_skipped(self):
        unknown = _wire._key(99, _wire.LENGTH) + b"\x03abc" + _wire._key(98, _wire.VARINT) + b"\x05"
        data = unknown + _wire.encode("HostFrame", {"id": 3})
        self.assertEqual({"id": 3}, _wire.decode("HostFrame", data))

    def test_default_values_are_not_written(self):
        self.assertEqual(b"", _wire.encode("Pong", {"connected": False, "state": "", "in_flight": 0}))


class HpackTests(unittest.TestCase):
    def test_rfc7541_huffman_examples(self):
        # RFC 7541, C.4.1 and C.6.1.
        self.assertEqual(b"www.example.com", _hpack.huffman_decode(bytes.fromhex("f1e3c2e5f23a6ba0ab90f4ff")))
        self.assertEqual(b"Mon, 21 Oct 2013 20:13:21 GMT",
                         _hpack.huffman_decode(bytes.fromhex("d07abe941054d444a8200595040b8166e082a62d1bff")))

    def test_rfc7541_response_sequence_with_huffman_and_the_dynamic_table(self):
        # RFC 7541, C.6: three responses on one decoder, the table evicting as it fills.
        decoder = _hpack.Decoder(max_size=256)
        first = decoder.decode(bytes.fromhex(
            "488264025885aec3771a4b6196d07abe941054d444a8200595040b8166e082a62d1bff"
            "6e919d29ad171863c78f0b97c8e9ae82ae43d3"))
        self.assertEqual([(":status", "302"), ("cache-control", "private"),
                          ("date", "Mon, 21 Oct 2013 20:13:21 GMT"), ("location", "https://www.example.com")], first)
        second = decoder.decode(bytes.fromhex("4883640effc1c0bf"))
        self.assertEqual(":status", second[0][0])
        self.assertEqual("307", second[0][1])
        self.assertEqual(("location", "https://www.example.com"), second[3])

    def test_literals_encode_and_decode(self):
        headers = [(":path", "/sw.serverless.v1.AdapterHost/Attach"), ("te", "trailers")]
        self.assertEqual(headers, _hpack.Decoder().decode(_hpack.encode(headers)))


if __name__ == "__main__":
    unittest.main()
