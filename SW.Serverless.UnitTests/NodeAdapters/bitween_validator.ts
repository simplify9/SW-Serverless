// A Bitween validator in TypeScript: run as Node runs it once serverless build strips the types.
const sw = require("@simplyworks/serverless");
const { ExchangeFile, ValidationResult, Validator } = require("@simplyworks/bitween");

interface Order { orderId?: string; lines?: unknown[] }

class Orders extends Validator {
  validate(file: typeof ExchangeFile.prototype): typeof ValidationResult.prototype {
    const order: Order = JSON.parse(file.data);
    const result = new ValidationResult();
    if (!order.orderId) result.add("orderId", "An order needs an id.");
    if (!order.lines || order.lines.length === 0) result.add("lines", "An order needs at least one line.");
    return result;
  }
}

sw.run(Orders);
