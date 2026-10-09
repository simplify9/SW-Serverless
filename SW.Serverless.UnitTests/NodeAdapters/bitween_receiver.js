// A Bitween receiver over a folder, written with @simplyworks/bitween.
const fs = require("node:fs");
const path = require("node:path");
const sw = require("@simplyworks/serverless");
const { ExchangeFile, Receiver } = require("@simplyworks/bitween");

class Folder extends Receiver {
  constructor() {
    super();
    sw.expect("Folder");
    this.calls = [];
  }

  get folder() { return sw.valueOf("Folder"); }
  initialize() { this.calls.push("Initialize"); }
  listFiles() { this.calls.push("ListFiles"); return fs.readdirSync(this.folder).sort(); }
  getFile(id) {
    this.calls.push("GetFile");
    return new ExchangeFile({ data: fs.readFileSync(path.join(this.folder, id), "utf8"), filename: id });
  }
  deleteFile(id) { this.calls.push("DeleteFile"); fs.rmSync(path.join(this.folder, id)); }
  finalize() {
    this.calls.push("Finalize");
    fs.writeFileSync(path.join(this.folder, "..", "calls.txt"), this.calls.join(","));
  }
}

sw.run(Folder);
