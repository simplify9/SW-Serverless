// A processor for the tests' sample "orders" contract, in JavaScript.
const sw = require("@simplyworks/sw-serverless");

class Processor {
  static kinds = ["processor"];
  static contracts = { orders: 1 };
  static commands = {
    Process: { method: "process", input: "json", output: "json", description: "Takes an order and says whether it was accepted." },
  };

  constructor() {
    sw.expect("Partner", { description: "Who receives the orders" });
  }

  process(order) {
    if (!order.Lines) return { Accepted: false, Reference: null };
    return { Accepted: true, Reference: `${sw.valueOf("Partner")}:${order.OrderId}` };
  }
}

sw.run(Processor);
