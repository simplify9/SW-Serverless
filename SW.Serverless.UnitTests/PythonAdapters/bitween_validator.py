"""A Bitween validator: an order needs an id and at least one line."""
import json

import simplyworks_serverless as sw
from simplyworks_bitween import ExchangeFile, ValidationResult, Validator


class Orders(Validator):
    def validate(self, file: ExchangeFile) -> ValidationResult:
        order = json.loads(file.data)
        result = ValidationResult()
        if not order.get("orderId"):
            result.add("orderId", "An order needs an id.")
        if not order.get("lines"):
            result.add("lines", "An order needs at least one line.")
        return result


if __name__ == "__main__":
    sw.run(Orders)
