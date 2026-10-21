import type { Skipped } from "../core/handlers.ts";
import type { FileRef } from "../core/models.ts";
import type { DeclaredSecrets } from "../core/secrets.ts";

/** Facts about the current execution, from the service (see README "Environment"). */
export interface RunInfo {
  /** Pipeline id (`OLVE_PIPELINE`). */
  readonly pipeline: string;
  /** Run id (`OLVE_RUN_ID`). */
  readonly id: string;
  /** Run number, monotonic per pipeline (`OLVE_RUN`). */
  readonly number: number;
  /** Attempt number of this step in the run, from 1 (`OLVE_ATTEMPT`). */
  readonly attempt: number;
  /** Name of the step being executed. */
  readonly step: string;
  /** Version stamp derived from the run, e.g. `2026.10.21.412` (`OLVE_VERSION_STAMP`). */
  readonly version: string;
}

export interface ShOptions {
  /** Extra environment variables (merged over the harness environment). */
  env?: Record<string, string>;
  /** Working directory (default: the harness's cwd). */
  cwd?: string;
  /** Arguments passed to an embedded script (`$1`, `$2`, …). */
  args?: readonly string[];
}
/**
 * Run a command string or an embedded script through the image's shell.
 * A string is treated as an embedded script when it is a path inside the compiled binary
 * (`/$bunfs/…`) or an absolute path to an existing `*.sh` / `*.bash` file — i.e. the value of
 * `import s from "./x.sh" with { type: "file" }`. Pass `{ script }` to force script mode.
 * Rejects with an error carrying the exit code when the command fails.
 */
export interface Sh {
  (command: string | { script: string }, opts?: ShOptions): Promise<void>;
  /** Like `sh`, but captures stdout (trimmed) instead of streaming it. */
  text(command: string | { script: string }, opts?: ShOptions): Promise<string>;
}
export interface ExecOptions {
  env?: Record<string, string>;
  cwd?: string;
}

export interface HttpInit {
  headers?: Record<string, string>;
}
export interface Http {
  /** `fetch` bound to `ctx.signal`. */
  fetch(url: string, init?: RequestInit): Promise<Response>;
  /** GET and parse JSON; rejects on a non-2xx status. */
  getJson<T = unknown>(url: string, init?: HttpInit): Promise<T>;
  /** GET (or `init.method`) and reject unless the status is 2xx. */
  expectOk(url: string, init?: HttpInit & { method?: string }): Promise<Response>;
}

export interface RetryOptions {
  attempts: number;
  delaySeconds: number;
  /** Multiplier applied to the delay after every failure (default 1 = constant). */
  backoff?: number;
}

export interface OutputFileOptions {
  /** Name inside the step's files (default: the source's base name). May contain `/`. */
  name?: string;
}

export interface Ctx<Sec extends string = string> {
  /** Exactly the secrets the step declares, read from the environment. */
  readonly secrets: DeclaredSecrets<Sec>;
  readonly sh: Sh;
  /** Run a binary directly, without a shell (works in shell-less images). Rejects on non-zero exit. */
  exec(argv: readonly string[], opts?: ExecOptions): Promise<void>;
  readText(path: string): Promise<string>;
  readJson<T = unknown>(path: string): Promise<T>;
  readonly files: {
    /** Download (and verify) a referenced file; directory refs are extracted. Returns the local path. */
    fetch(ref: FileRef): Promise<string>;
  };
  readonly output: {
    /** Add an existing file to the step's output files. */
    file(path: string, opts?: OutputFileOptions): Promise<FileRef>;
    /** Add an existing directory (archived as a deterministic tar) to the step's output files. */
    dir(path: string, opts?: OutputFileOptions): Promise<FileRef>;
    /** Write content as an output file. */
    write(name: string, content: string | Uint8Array): Promise<FileRef>;
  };
  readonly http: Http;
  retry<T>(opts: RetryOptions, fn: (attempt: number) => Promise<T>): Promise<T>;
  /** Structured log line on stderr. */
  log(message: string, fields?: Record<string, unknown>): void;
  /** Aborted on SIGTERM/SIGINT (cancellation, `activeDeadlineSeconds`). */
  readonly signal: AbortSignal;
  readonly run: RunInfo;
  /** A writable scratch directory (`$OLVE_DIR/tmp`; images like kaniko have no /tmp). */
  readonly tmpDir: string;
  /** Runtime escape hatch: produce no output. Prefer `when` blocks. */
  skip(reason: string): Skipped;
}
