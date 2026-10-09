// A source for the tests' sample "orders" contract, in JavaScript: the order files in a folder.
const fs = require("node:fs");
const path = require("node:path");
const sw = require("@simplyworks/sw-serverless");

class Source {
  static kinds = ["source"];
  static contracts = { orders: 1 };
  static commands = {
    Open: { method: "open" },
    List: { method: "list", output: "json" },
    Fetch: { method: "fetch", input: "string", output: "json" },
    Remove: { method: "remove", input: "string" },
    Close: { method: "close" },
  };

  constructor() {
    sw.expect("Folder");
    this.calls = [];
  }

  get folder() { return sw.valueOf("Folder"); }
  open() { this.calls.push("Open"); }
  list() { this.calls.push("List"); return fs.readdirSync(this.folder).sort(); }
  fetch(id) {
    this.calls.push("Fetch");
    return { OrderId: id, Lines: fs.readFileSync(path.join(this.folder, id), "utf8").length };
  }
  remove(id) { this.calls.push("Remove"); fs.rmSync(path.join(this.folder, id)); }
  close() {
    this.calls.push("Close");
    fs.writeFileSync(path.join(this.folder, "..", "calls.txt"), this.calls.join(","));
  }
}

sw.run(Source);
