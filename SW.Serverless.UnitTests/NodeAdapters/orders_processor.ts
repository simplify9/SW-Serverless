// A processor for the tests' sample "orders" contract, in TypeScript: run as Node runs it once the build strips the types.
const sw = require("@simplyworks/sw-serverless");

interface Order { OrderId: string; Lines?: number }
interface Receipt { Accepted: boolean; Reference: string | null }

class Processor {
  static kinds = ["processor"];
  static contracts = { orders: 1 };
  static commands = {
    Process: { method: "process", input: "json", output: "json" },
  };

  constructor() {
    sw.expect("Partner", { description: "Who receives the orders" });
  }

  process(order: Order): Receipt {
    return order.Lines ? { Accepted: true, Reference: `${sw.valueOf("Partner")}:${order.OrderId}` } : { Accepted: false, Reference: null };
  }
}

sw.run(Processor);
