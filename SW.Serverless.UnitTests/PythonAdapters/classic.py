"""A classic adapter in Python, called one session, one call at a time."""
import asyncio
import logging
from dataclasses import dataclass

import sw_serverless as sw


@dataclass
class Sum:
    A: int
    B: int


class Classic:
    def __init__(self):
        sw.expect("Prefix", description="Put before every greeting")
        sw.expect("Secret", "s3cret", secret=True)

    @sw.command("Greet", description="Greets someone")
    def greet(self, name: str) -> str:
        return sw.value_of("Prefix") + name

    @sw.command("Correlation")
    def correlation(self) -> str:
        return sw.value_of("CorrelationId")

    @sw.command("Add")
    async def add(self, numbers: Sum) -> int:
        return numbers.A + numbers.B

    @sw.command("Fail")
    def fail(self, message: str) -> str:
        raise sw.AdapterError(message, type="Acme.Rejected")

    @sw.command("Crash")
    def crash(self) -> str:
        return {}["missing"]

    @sw.command("Big")
    def big(self, size: int) -> str:
        return "x" * size

    @sw.command("Length")
    def length(self, text: str) -> int:
        return len(text)

    @sw.command("Bytes")
    def bytes_(self, data: bytes) -> bytes:
        return bytes(reversed(data))

    @sw.command("Nothing")
    def nothing(self) -> None:
        logging.getLogger("classic").info("did nothing, as asked")

    @sw.command("Slow")
    async def slow(self, seconds: float) -> str:
        await asyncio.sleep(seconds)
        return "finished"

    @sw.command("Property")
    def property_(self, name: str) -> str:
        return sw.value_of(name, "")


if __name__ == "__main__":
    sw.run(Classic)
