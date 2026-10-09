/** A command's argument or result: raw text, raw bytes, JSON, or JSON described by a JSON Schema. */
export type PayloadSpec = "string" | "bytes" | "json" | Record<string, unknown>;

export interface CommandSpec {
  /** The method on the adapter that runs it. */
  method: string;
  /** Omitted when the command takes no argument. */
  input?: PayloadSpec;
  /** Omitted when it returns nothing. */
  output?: PayloadSpec;
  description?: string;
}

/** Declared on an adapter class as `static commands`, keyed by the name the host calls. */
export type Commands = Record<string, CommandSpec>;

export interface SettingOptions {
  default?: string | number | boolean;
  /** Required unless it has a default. */
  required?: boolean;
  /** Masked wherever it is shown. */
  secret?: boolean;
  description?: string;
  type?: "text" | "multiline" | "number" | "boolean" | "select" | "json";
}

export declare function expect(name: string, options?: SettingOptions): string;
/** The call's own properties, then the startup values, then the declared default, then `fallback`. */
export declare function valueOf(name: string, fallback?: string): string | undefined;
export declare function startupValues(): Record<string, string>;

export declare class AdapterError extends Error {
  constructor(message: string, options?: { type?: string; detail?: string });
  type?: string;
  detail?: string;
}

export interface Status {
  connected?: boolean;
  state?: string;
  inFlight?: number;
  lastError?: string;
  lastMessageOn?: Date | string | number;
  details?: Record<string, string | number | boolean>;
}

export declare class Context {
  readonly sessionId: string | null;
  readonly command: string | null;
  /** Aborted when the host gives up on the call. */
  readonly signal: AbortSignal;
  readonly adapterId: string;
  readonly instanceKey: string;
  /** Aborted once the host has asked the adapter to stop. */
  readonly stopping: AbortSignal;
  valueOf(name: string, fallback?: string): string | undefined;
  publish(payload: unknown, options?: { dedupeKey?: string; contentType?: string; headers?: Record<string, string>; endpoint?: string }): Promise<string>;
  getState(name: string): Promise<string | null>;
  setState(name: string, value: string): Promise<void>;
  deleteState(name: string): Promise<void>;
  metric(name: string, value: number, tags?: Record<string, string>): void;
}

export declare function context(): Context;

type LogFn = (message: unknown, error?: unknown) => void;
export declare const log: { trace: LogFn; debug: LogFn; info: LogFn; warn: LogFn; error: LogFn; critical: LogFn };

/**
 * An adapter class: `static commands` names what the host can call. A `start` method makes it
 * resident, with optional `stop`, `status` and `reset(sessionId)`.
 */
export interface AdapterClass {
  new (): object;
  commands?: Commands;
}

export declare function describe(adapter: AdapterClass): Record<string, unknown>;
export declare function run(adapter: AdapterClass): Promise<void>;
export declare const SDK_VERSION: string;
