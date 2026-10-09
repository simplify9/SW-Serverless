// A Bitween handler written with @simplyworks/bitween.
const sw = require("@simplyworks/serverless");
const { ExchangeFile, Handler } = require("@simplyworks/bitween");

class Orders extends Handler {
  constructor() {
    super();
    sw.expect("Partner", { description: "Who receives the orders" });
  }

  handle(file) {
    const order = JSON.parse(file.data);
    if (order.reject) return new ExchangeFile({ data: '{"error":"rejected"}', badData: true, contentType: "application/json" });
    const answer = { to: sw.valueOf("Partner"), orderId: order.orderId, from: file.filename };
    return new ExchangeFile({ data: JSON.stringify(answer), filename: "answer.json", contentType: "application/json" });
  }
}

sw.run(Orders);
