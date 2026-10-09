import dataclasses
import json
import unittest

import simplyworks_serverless as sw
from simplyworks_serverless import _adapter, _types


@dataclasses.dataclass
class Point:
    X: int
    Y: int = 0


class TypesTests(unittest.TestCase):
    def test_strings_are_raw_text_and_objects_json(self):
        self.assertEqual(b"h\xc3\xa9", _types.encode("hé"))
        self.assertEqual("hé", _types.decode(str, "hé".encode()))
        self.assertEqual(b'{"X":1,"Y":2}', _types.encode(Point(1, 2)))
        self.assertEqual(Point(3, 0), _types.decode(Point, b'{"X":3,"Ignored":true}'))
        self.assertEqual(b"", _types.encode(None))
        self.assertEqual(b"\x01\x02", _types.decode(bytes, b"\x01\x02"))
        self.assertEqual([1, 2], _types.decode(list[int], b"[1,2]"))

    def test_schemas(self):
        self.assertEqual({"type": "string"}, _types.schema(str))
        self.assertEqual({"type": "object", "properties": {"X": {"type": "integer"}, "Y": {"type": "integer"}},
                          "required": ["X"]}, _types.schema(Point))
        self.assertEqual({"type": "array", "items": {"type": "string"}}, _types.schema(list[str]))


class Example:
    def __init__(self):
        sw.expect("Url", description="Where to send")
        sw.expect("Retries", "3", type="number")
        sw.expect("Key", secret=True, required=False)

    @sw.command("Send", description="Sends it")
    def send(self, point: Point) -> str:
        return "sent"

    @sw.command
    def ping(self) -> None:
        pass


class DescribeTests(unittest.TestCase):
    def setUp(self):
        _adapter._settings.clear()

    def test_describes_settings_commands_and_lifecycle(self):
        described = sw.describe(Example)
        self.assertEqual("python", described["sdkLanguage"])
        self.assertEqual("classic", described["lifecycle"])
        self.assertEqual({"min": 2, "max": 2}, described["protocol"])
        settings = {s["name"]: s for s in described["settings"]}
        self.assertTrue(settings["Url"]["required"])
        self.assertFalse(settings["Retries"]["required"])
        self.assertEqual("3", settings["Retries"]["default"])
        self.assertEqual("number", settings["Retries"]["type"])
        self.assertTrue(settings["Key"]["secret"])
        self.assertFalse(settings["Key"]["required"])
        commands = {c["name"]: c for c in described["commands"]}
        self.assertEqual("Sends it", commands["Send"]["description"])
        self.assertEqual("object", commands["Send"]["inputSchema"]["type"])
        self.assertTrue(commands["Send"]["returnsValue"])
        self.assertFalse(commands["ping"]["returnsValue"])
        self.assertIsNone(commands["ping"]["inputSchema"])
        json.dumps(described)

    def test_an_adapter_with_a_start_hook_is_resident(self):
        class Listener:
            async def start(self):
                pass
        self.assertEqual("resident", sw.describe(Listener)["lifecycle"])

    def test_an_adapter_that_cannot_be_built_is_still_described_with_a_warning(self):
        class NeedsArgs:
            def __init__(self, required):
                pass
        described = sw.describe(NeedsArgs)
        self.assertEqual(1, len(described["warnings"]))

    def test_a_command_takes_at_most_one_argument(self):
        class TwoArgs:
            @sw.command
            def both(self, a: str, b: str) -> str:
                return a + b
        with self.assertRaises(TypeError):
            _adapter.commands_of(TwoArgs())

    def test_values_come_from_the_call_then_startup_then_the_default(self):
        sw.expect("Mode", "fast")
        self.assertEqual("fast", sw.value_of("Mode"))
        _adapter._startup_values["Mode"] = "slow"
        try:
            self.assertEqual("slow", sw.value_of("Mode"))
            token = _adapter._call_values.set({"Mode": "per-call"})
            self.assertEqual("per-call", sw.value_of("Mode"))
            _adapter._call_values.reset(token)
        finally:
            _adapter._startup_values.clear()
        self.assertIsNone(sw.value_of("Missing"))
        self.assertEqual("x", sw.value_of("Missing", "x"))


if __name__ == "__main__":
    unittest.main()
