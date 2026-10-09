"""A source for the tests' sample "orders" contract: the order files in a folder, one session per run."""
import os

import sw_serverless as sw


@sw.implements("orders", 1, "source")
class Source:
    def __init__(self):
        sw.expect("Folder")
        self.calls = []

    @property
    def folder(self):
        return sw.value_of("Folder")

    @sw.command("Open")
    def open(self) -> None:
        self.calls.append("Open")

    @sw.command("List")
    def list(self) -> list[str]:
        self.calls.append("List")
        return sorted(os.listdir(self.folder))

    @sw.command("Fetch")
    def fetch(self, order_id: str) -> dict:
        self.calls.append("Fetch")
        with open(os.path.join(self.folder, order_id), encoding="utf-8") as f:
            return {"OrderId": order_id, "Lines": len(f.read())}

    @sw.command("Remove")
    def remove(self, order_id: str) -> None:
        self.calls.append("Remove")
        os.remove(os.path.join(self.folder, order_id))

    @sw.command("Close")
    def close(self) -> None:
        self.calls.append("Close")
        with open(os.path.join(self.folder, "..", "calls.txt"), "w", encoding="utf-8") as f:
            f.write(",".join(self.calls))


if __name__ == "__main__":
    sw.run(Source)
