// A classic adapter in JavaScript, called one session, one call at a time.
const sw = require("@simplyworks/sw-serverless");

class Classic {
  static commands = {
    Greet: { method: "greet", input: "string", output: "string", description: "Greets someone" },
    Correlation: { method: "correlation", output: "string" },
    Add: { method: "add", input: { type: "object", properties: { A: { type: "integer" }, B: { type: "integer" } } }, output: { type: "integer" } },
    Fail: { method: "fail", input: "string", output: "string" },
    Crash: { method: "crash", output: "string" },
    Big: { method: "big", input: "json", output: "string" },
    Length: { method: "length", input: "string", output: "json" },
    Bytes: { method: "bytes", input: "bytes", output: "bytes" },
    Nothing: { method: "nothing" },
    Slow: { method: "slow", input: "json", output: "string" },
  };

  constructor() {
    sw.expect("Prefix", { description: "Put before every greeting" });
    sw.expect("Secret", { default: "s3cret", secret: true });
  }

  greet(name) { return sw.valueOf("Prefix") + name; }
  correlation() { return sw.valueOf("CorrelationId"); }
  async add({ A, B }) { return A + B; }
  fail(message) { throw new sw.AdapterError(message, { type: "Acme.Rejected" }); }
  crash() { return undefined.missing; }
  big(size) { return "x".repeat(size); }
  length(text) { return text.length; }
  bytes(data) { return Buffer.from(data).reverse(); }
  nothing() { sw.log.info("did nothing, as asked"); }
  slow(seconds) {
    const signal = sw.context().signal;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => resolve("finished"), seconds * 1000);
      signal.addEventListener("abort", () => { clearTimeout(timer); reject(new Error("cancelled")); });
    });
  }
}

sw.run(Classic);
