export declare const CONTRACT: "bitween";
export declare const CONTRACT_VERSION: 1;

export declare class ExchangeFile {
  constructor(init?: { data?: string; filename?: string | null; badData?: boolean; contentType?: string | null });
  /** The content: text, or base64 for binary content. */
  data: string;
  filename: string | null;
  /** A bad response: a delivery the partner rejected. Returned, not thrown. */
  badData: boolean;
  contentType: string | null;
  /** SHA-1 of data as lower-case hex. */
  readonly hash: string;
  toWire(): Record<string, unknown>;
  static fromWire(value: unknown): ExchangeFile;
}

export declare class ValidationResult {
  constructor(validations?: [string, string][]);
  validations: [string, string][];
  readonly success: boolean;
  add(key: string, message: string): this;
  toWire(): Record<string, unknown>;
}

type Awaitable<T> = T | Promise<T>;

export declare abstract class Handler {
  abstract handle(file: ExchangeFile): Awaitable<ExchangeFile>;
}

export declare abstract class Mapper {
  abstract map(file: ExchangeFile): Awaitable<ExchangeFile>;
}

export declare abstract class Validator {
  abstract validate(file: ExchangeFile): Awaitable<ValidationResult | [string, string][] | Record<string, string> | null | void>;
}

export declare abstract class Receiver {
  initialize(): Awaitable<void>;
  abstract listFiles(): Awaitable<string[]>;
  abstract getFile(fileId: string): Awaitable<ExchangeFile>;
  abstract deleteFile(fileId: string): Awaitable<void>;
  finalize(): Awaitable<void>;
}
