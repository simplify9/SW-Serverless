"""Write SW-Serverless adapters in Python.

    import simplyworks_serverless as sw

    class Greeter:
        def __init__(self):
            sw.expect("Greeting", "Hello", description="What to say")

        @sw.command(description="Greets someone")
        def greet(self, name: str) -> str:
            return f"{sw.value_of('Greeting')}, {name}"

    if __name__ == "__main__":
        sw.run(Greeter)

``python adapter.py --describe`` prints what it is; the host runs it otherwise. An adapter with a
``start`` hook is resident: it runs until stopped, with ``stop``, ``status`` and ``reset`` hooks.
"""

from ._adapter import command, declared_settings, expect, startup_values, value_of
from ._runner import SDK_VERSION, AdapterError, Context, context, describe, run

__version__ = SDK_VERSION

__all__ = [
    "AdapterError", "Context", "SDK_VERSION", "command", "context", "declared_settings", "describe",
    "expect", "run", "startup_values", "value_of",
]
