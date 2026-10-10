import os
import subprocess
import sys
import unittest

from sw_serverless import _runner

VARIABLE = _runner.MEMORY_LIMIT_VARIABLE
SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src")


class FakeResource:
    RLIMIT_DATA = 2
    RLIM_INFINITY = -1

    def __init__(self, hard=-1):
        self.hard = hard
        self.set = []

    def getrlimit(self, which):
        return (self.hard, self.hard)

    def setrlimit(self, which, limits):
        self.set.append((which, limits))


class MemoryLimitTests(unittest.TestCase):
    def tearDown(self):
        _runner._memory_limit = None

    def test_on_linux_the_limit_becomes_rlimit_data_soft_and_hard(self):
        resource = FakeResource()
        applied = _runner.apply_memory_limit({VARIABLE: str(256 << 20)}, "linux", resource)
        self.assertEqual(256 << 20, applied)
        self.assertEqual([(FakeResource.RLIMIT_DATA, (256 << 20, 256 << 20))], resource.set)
        self.assertEqual(256 << 20, _runner._memory_limit)

    def test_a_lower_hard_limit_already_in_place_is_kept(self):
        resource = FakeResource(hard=100 << 20)
        self.assertEqual(100 << 20, _runner.apply_memory_limit({VARIABLE: str(256 << 20)}, "linux", resource))
        self.assertEqual([(FakeResource.RLIMIT_DATA, (100 << 20, 100 << 20))], resource.set)

    def test_without_a_limit_nothing_is_set(self):
        for environ in ({}, {VARIABLE: ""}, {VARIABLE: "0"}, {VARIABLE: "lots"}):
            resource = FakeResource()
            self.assertIsNone(_runner.apply_memory_limit(environ, "linux", resource), environ)
            self.assertEqual([], resource.set)
            self.assertIsNone(_runner._memory_limit)

    def test_off_linux_nothing_is_set(self):
        # macOS doesn't enforce RLIMIT_DATA (nor RLIMIT_AS, reliably); the host's watchdog holds it there.
        for platform in ("darwin", "win32"):
            resource = FakeResource()
            self.assertIsNone(_runner.apply_memory_limit({VARIABLE: str(256 << 20)}, platform, resource))
            self.assertEqual([], resource.set)

    @unittest.skipUnless(sys.platform.startswith("linux"), "RLIMIT_DATA is enforced on Linux only")
    def test_on_linux_an_allocation_past_the_limit_raises_memory_error(self):
        script = (
            "from sw_serverless import _runner\n"
            "print(_runner.apply_memory_limit())\n"
            "try:\n"
            "    hog = bytearray(512 << 20)\n"
            "    print('allocated')\n"
            "except MemoryError:\n"
            "    print('MemoryError')\n"
        )
        environ = dict(os.environ, PYTHONPATH=SRC)
        limited = subprocess.run([sys.executable, "-c", script], env=dict(environ, **{VARIABLE: str(128 << 20)}),
                                 capture_output=True, text=True, timeout=60)
        self.assertEqual(["134217728", "MemoryError"], limited.stdout.split(), limited.stderr)

        environ.pop(VARIABLE, None)
        unlimited = subprocess.run([sys.executable, "-c", script], env=environ, capture_output=True, text=True, timeout=60)
        self.assertEqual(["None", "allocated"], unlimited.stdout.split(), unlimited.stderr)


if __name__ == "__main__":
    unittest.main()
