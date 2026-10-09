"""A Bitween handler, mapper-free: the contract's Handle, written with simplyworks_bitween."""
import json

import simplyworks_serverless as sw
from simplyworks_bitween import ExchangeFile, Handler


class Orders(Handler):
    def __init__(self):
        sw.expect("Partner", description="Who receives the orders")

    def handle(self, file: ExchangeFile) -> ExchangeFile:
        order = json.loads(file.data)
        if order.get("reject"):
            return ExchangeFile(data='{"error":"rejected"}', bad_data=True, content_type="application/json")
        answer = {"to": sw.value_of("Partner"), "orderId": order["orderId"], "from": file.filename}
        return ExchangeFile(data=json.dumps(answer), filename="answer.json", content_type="application/json")


if __name__ == "__main__":
    sw.run(Orders)
