// A resident adapter in JavaScript: started, kept running, asked for its status, reset and stopped.
const sw = require("@simplyworks/sw-serverless");

class Resident {
  static commands = {
    Started: { method: "isStarted", output: "json" },
    Publish: { method: "publish", input: "string", output: "string" },
    Remember: { method: "remember", input: "string", output: "string" },
    Forget: { method: "forget" },
    Resets: { method: "getResets", output: "json" },
  };

  constructor() {
    this.started = false;
    this.resets = [];
    sw.expect("Name", { default: "resident" });
  }

  async start() {
    // Asking the host from start: answered only because start runs beside the read loop.
    await sw.context().setState("started", "yes");
    this.started = true;
  }
  async stop() { this.started = false; }
  status() { return { connected: this.started, state: "Listening", details: { resets: this.resets.length } }; }
  reset(sessionId) { this.resets.push(sessionId); }

  async isStarted() { return this.started && (await sw.context().getState("started")) === "yes"; }
  publish(text) {
    return sw.context().publish(text, { dedupeKey: "k-" + text, contentType: "text/plain", endpoint: "tests" });
  }
  async remember(value) {
    const ctx = sw.context();
    const before = await ctx.getState("memory");
    await ctx.setState("memory", value);
    return before ?? "";
  }
  async forget() { await sw.context().deleteState("memory"); }
  getResets() { return this.resets; }
}

sw.run(Resident);
