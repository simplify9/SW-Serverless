"use strict";
/**
 * The Bitween adapter contract for JavaScript and TypeScript: the kinds of adapter Bitween runs, and
 * what it passes. Extend a kind and implement its methods; the wire names, the encoding and the kind
 * and contract declarations are taken care of. The contract itself is bitween-adapter-contract.v1.json
 * in SW.Bitween.Adapters; this is its JavaScript form.
 *
 *   const sw = require("@simplyworks/serverless");
 *   const { ExchangeFile, Handler } = require("@simplyworks/bitween");
 *
 *   class Orders extends Handler {
 *     constructor() { super(); sw.expect("Url", { description: "Where orders go" }); }
 *     handle(file) { return new ExchangeFile({ data: file.data, filename: file.filename }); }
 *   }
 *
 *   sw.run(Orders);
 */

const crypto = require("node:crypto");

const CONTRACT = "bitween";
const CONTRACT_VERSION = 1;
const VERSION = "10.0.59";

const EXCHANGE_FILE_SCHEMA = {
  title: "ExchangeFile", type: "object", required: ["Data"], additionalProperties: true,
  properties: {
    Filename: { type: ["string", "null"] }, Data: { type: "string" }, Hash: { type: ["string", "null"] },
    BadData: { type: "boolean", default: false }, ContentType: { type: ["string", "null"] },
  },
};

const VALIDATION_RESULT_SCHEMA = {
  title: "ValidationResult", type: "object", required: ["Validations"], additionalProperties: true,
  properties: {
    Success: { type: "boolean" },
    Validations: {
      type: "array",
      items: { type: "object", required: ["Key", "Value"], properties: { Key: { type: "string" }, Value: { type: "string" } } },
    },
  },
};

/**
 * A file as Bitween passes it to and from adapters. `data` is the content: text, or base64 for
 * binary content. `badData` marks a bad response, a delivery the partner rejected — returned, not thrown.
 */
class ExchangeFile {
  constructor({ data = "", filename = null, badData = false, contentType = null } = {}) {
    this.data = data;
    this.filename = filename;
    this.badData = badData;
    this.contentType = contentType;
  }

  /** SHA-1 of `data` as lower-case hex, as .NET adapters write it. */
  get hash() {
    return crypto.createHash("sha1").update(this.data ?? "", "utf8").digest("hex");
  }

  toWire() {
    return { Filename: this.filename, Data: this.data ?? "", Hash: this.hash, BadData: this.badData, ContentType: this.contentType };
  }

  static fromWire(value) {
    if (!value || typeof value !== "object") throw new TypeError("an ExchangeFile is a JSON object");
    // Hash is recomputed, never trusted; unknown properties are ignored.
    return new ExchangeFile({
      data: value.Data ?? "", filename: value.Filename ?? null, badData: Boolean(value.BadData ?? false),
      contentType: value.ContentType ?? null,
    });
  }
}

/** What a validator found: each failure as a key — often the field it concerns — and a message. */
class ValidationResult {
  constructor(validations = []) {
    this.validations = validations;
  }

  get success() {
    return this.validations.length === 0;
  }

  add(key, message) {
    this.validations.push([key, message]);
    return this;
  }

  toWire() {
    return { Success: this.success, Validations: this.validations.map(([Key, Value]) => ({ Key, Value })) };
  }

  static fromWire(value) {
    return new ValidationResult(((value && value.Validations) || []).map((v) => [v.Key ?? "", v.Value ?? ""]));
  }
}

function asFile(value) {
  if (value instanceof ExchangeFile) return value;
  if (typeof value === "string") return new ExchangeFile({ data: value });
  if (value && typeof value === "object" && "data" in value) return new ExchangeFile(value);
  throw new TypeError(`expected an ExchangeFile, got ${value === null ? "null" : typeof value}`);
}

const ABSTRACT = Symbol("abstract");

function abstract(name) {
  const fn = function () { throw new Error(`${name} is not implemented`); };
  fn[ABSTRACT] = true;
  return fn;
}

class Kind {
  static contracts = { [CONTRACT]: CONTRACT_VERSION };
  static required = [];

  /** A declared kind without its methods fails when the adapter starts, not on first use. */
  __swCheck() {
    const missing = this.constructor.required.filter((m) => this[m] && this[m][ABSTRACT]);
    if (missing.length)
      throw new TypeError(`${this.constructor.name} is a Bitween ${this.constructor.kinds[0]} but does not implement ${missing.join(", ")}`);
  }
}

const file = EXCHANGE_FILE_SCHEMA;

/** Delivers a message and returns the partner's response. A rejection is returned with badData, not thrown. */
class Handler extends Kind {
  static kinds = ["handler"];
  static required = ["handle"];
  static commands = {
    Handle: { method: "__swHandle", input: file, output: file, description: "Delivers a message and returns the partner's response." },
  };

  async __swHandle(value) {
    return asFile(await this.handle(ExchangeFile.fromWire(value)));
  }
}
Handler.prototype.handle = abstract("handle");

/** Transforms a message into the shape the next step expects. */
class Mapper extends Kind {
  static kinds = ["mapper"];
  static required = ["map"];
  static commands = {
    Handle: { method: "__swHandle", input: file, output: file, description: "Transforms a message into the shape the next step expects." },
  };

  async __swHandle(value) {
    return asFile(await this.map(ExchangeFile.fromWire(value)));
  }
}
Mapper.prototype.map = abstract("map");

/** Checks a message before it is accepted. */
class Validator extends Kind {
  static kinds = ["validator"];
  static required = ["validate"];
  static commands = {
    Validate: { method: "__swValidate", input: file, output: VALIDATION_RESULT_SCHEMA, description: "Checks a message before it is accepted." },
  };

  async __swValidate(value) {
    const result = await this.validate(ExchangeFile.fromWire(value));
    if (result === undefined || result === null) return new ValidationResult();
    if (result instanceof ValidationResult) return result;
    // An array of [key, message] pairs, or an object of key -> message, reads naturally too.
    return new ValidationResult(Array.isArray(result) ? result : Object.entries(result));
  }
}
Validator.prototype.validate = abstract("validate");

/**
 * Fetches files from a source on a schedule. One session per run: initialize, listFiles, then for
 * each file getFile and — once it is safely taken in — deleteFile, and finally finalize, which is
 * also called after a failure.
 */
class Receiver extends Kind {
  static kinds = ["receiver"];
  static required = ["listFiles", "getFile", "deleteFile"];
  static commands = {
    Initialize: { method: "__swInitialize", description: "Starts a run." },
    ListFiles: { method: "__swListFiles", output: { type: "array", items: { type: "string" } }, description: "The ids of the files waiting." },
    GetFile: { method: "__swGetFile", input: "string", output: file, description: "One file, by an id ListFiles gave." },
    DeleteFile: { method: "__swDeleteFile", input: "string", description: "Removes a file from the source once it is safely taken in." },
    Finalize: { method: "__swFinalize", description: "Ends a run, after a failure too." },
  };

  initialize() {}
  finalize() {}

  async __swInitialize() { await this.initialize(); }
  async __swListFiles() { return ((await this.listFiles()) || []).map(String); }
  async __swGetFile(fileId) { return asFile(await this.getFile(fileId)); }
  async __swDeleteFile(fileId) { await this.deleteFile(fileId); }
  async __swFinalize() { await this.finalize(); }
}
Receiver.prototype.listFiles = abstract("listFiles");
Receiver.prototype.getFile = abstract("getFile");
Receiver.prototype.deleteFile = abstract("deleteFile");

module.exports = { CONTRACT, CONTRACT_VERSION, VERSION, ExchangeFile, Handler, Mapper, Receiver, ValidationResult, Validator };
