# simplyworks-serverless

Write SW-Serverless adapters in Python 3.12 or later. The package has no dependencies: it speaks the
host's protocol — gRPC over a Unix socket — with the standard library alone, so it vendors into an
adapter package as plain files, for any platform.

```python
import simplyworks_serverless as sw


class Greeter:
    def __init__(self):
        sw.expect("Greeting", "Hello", description="What to say")
        sw.expect("ApiKey", secret=True)

    @sw.command(description="Greets someone")
    def greet(self, name: str) -> str:
        return f"{sw.value_of('Greeting')}, {name}"


if __name__ == "__main__":
    sw.run(Greeter)
```

- **Settings** are declared with `sw.expect(name, default=None, *, required, secret, description, type)`
  and read with `sw.value_of(name)`: the call's own properties, then the values the adapter was
  started with, then the default.
- **Commands** are methods marked `@sw.command(name, description=...)`. A command takes at most one
  argument. A `str` argument or result is raw text, `bytes` are passed as they are, and anything else
  is JSON; a dataclass is read from and written as a JSON object. Commands may be sync or async; sync
  ones run on a worker thread.
- **Errors** raised by a command reach the caller with their type and message. Raise
  `sw.AdapterError(message, type="Acme.Rejected")` to choose the type.
- **Resident adapters** have a `start` method and run until stopped, with optional `stop`, `status`
  (returns `connected`, `state`, `details`…) and `reset(session_id)`. In a command,
  `sw.context()` publishes events (`await ctx.publish(...)`), keeps small state
  (`get_state`/`set_state`/`delete_state`) and records metrics.
- **Logging** through Python's `logging` reaches the host.
- `python main.py --describe` prints what the adapter is; `serverless build` writes it into the
  manifest.

For Bitween adapters, `simplyworks-bitween` has the four kinds — `Handler`, `Mapper`, `Validator`,
`Receiver` — ready to subclass. `serverless init --lang python --kind handler` starts one.

Tests: `PYTHONPATH=src python -m unittest discover -s tests`.
