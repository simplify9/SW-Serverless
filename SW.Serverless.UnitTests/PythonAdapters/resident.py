"""A resident adapter in Python: started, kept running, asked for its status, reset and stopped."""
import sw_serverless as sw


class Resident:
    def __init__(self):
        self.started = False
        self.resets = []
        sw.expect("Name", "resident")

    async def start(self):
        # Asking the host from start: answered only because start runs beside the read loop.
        await sw.context().set_state("started", "yes")
        self.started = True

    async def stop(self):
        self.started = False

    def status(self):
        return {"connected": self.started, "state": "Listening", "details": {"resets": str(len(self.resets))}}

    def reset(self, session_id):
        self.resets.append(session_id)

    @sw.command("Started")
    async def is_started(self) -> bool:
        return self.started and await sw.context().get_state("started") == "yes"

    @sw.command("Publish")
    async def publish(self, text: str) -> str:
        return await sw.context().publish(text, dedupe_key="k-" + text, content_type="text/plain", endpoint="tests")

    @sw.command("Remember")
    async def remember(self, value: str) -> str:
        ctx = sw.context()
        before = await ctx.get_state("memory")
        await ctx.set_state("memory", value)
        return before or ""

    @sw.command("Forget")
    async def forget(self) -> None:
        await sw.context().delete_state("memory")

    @sw.command("Resets")
    def resets_(self) -> list[str]:
        return self.resets


if __name__ == "__main__":
    sw.run(Resident)
