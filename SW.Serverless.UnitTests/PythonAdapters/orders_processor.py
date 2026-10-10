"""A processor for the tests' sample "orders" contract: takes an order, answers with a receipt."""
import sw_serverless as sw


@sw.implements("orders", 1, "processor")
class Processor:
    def __init__(self):
        sw.expect("Partner", description="Who receives the orders")

    @sw.command("Process", description="Takes an order and says whether it was accepted.")
    def process(self, order: dict) -> dict:
        if not order.get("Lines"):
            return {"Accepted": False, "Reference": None}
        return {"Accepted": True, "Reference": f"{sw.value_of('Partner')}:{order['OrderId']}"}


if __name__ == "__main__":
    sw.run(Processor)
