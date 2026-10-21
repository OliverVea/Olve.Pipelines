import { mkdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { skipped } from "../core/handlers.ts";
import type { Secret } from "../core/secrets.ts";
import { createFetch, createOutput, redactUrl } from "./files.ts";
import { createSh } from "./sh.ts";
import type { Ctx, Http, RetryOptions, RunInfo } from "./types.ts";

export interface CtxOptions {
  run: RunInfo;
  /** Declared secrets of the step. */
  secrets: readonly Secret[];
  /** Secret values that take precedence over the environment (tests). */
  secretValues?: Readonly<Record<string, string>>;
  paths: { root: string; workFiles: string; tmp: string };
  signal: AbortSignal;
  /** Where log lines go (default stderr). */
  logSink?: (line: string) => void;
}

export function formatLog(message: string, fields?: Record<string, unknown>): string {
  const parts = Object.entries(fields ?? {}).map(([k, v]) => `${k}=${typeof v === "string" && !/\s|"/.test(v) ? v : JSON.stringify(v)}`);
  return parts.length ? `${message} ${parts.join(" ")}` : message;
}

export function createSecrets(declared: readonly Secret[], values?: Readonly<Record<string, string>>): Record<string, string> {
  const obj: Record<string, string> = {};
  for (const s of declared) {
    Object.defineProperty(obj, s.name, {
      enumerable: true,
      get() {
        const v = values?.[s.name] ?? process.env[s.name];
        if (v === undefined) throw new Error(`secret ${s.name} is not set (the harness reads it from the environment variable ${s.name})`);
        return v;
      },
    });
  }
  return Object.freeze(obj);
}

async function httpError(res: Response, url: string): Promise<Error> {
  let body = "";
  try {
    body = (await res.text()).slice(0, 500);
  } catch {
    /* ignore */
  }
  return new Error(`HTTP ${res.status} ${res.statusText} from ${redactUrl(url)}${body ? `: ${body}` : ""}`);
}

export function createHttp(signal: AbortSignal): Http {
  const f = (url: string, init?: RequestInit) => fetch(url, { ...init, signal: init?.signal ?? signal });
  return {
    fetch: f,
    async getJson<T>(url: string, init?: { headers?: Record<string, string> }): Promise<T> {
      const res = await f(url, { headers: { accept: "application/json", ...init?.headers } });
      if (!res.ok) throw await httpError(res, url);
      return (await res.json()) as T;
    },
    async expectOk(url: string, init?: { headers?: Record<string, string>; method?: string }): Promise<Response> {
      const res = await f(url, { method: init?.method ?? "GET", headers: init?.headers });
      if (!res.ok) throw await httpError(res, url);
      return res;
    },
  };
}

export function createRetry(signal: AbortSignal, log: (m: string, f?: Record<string, unknown>) => void) {
  return async <T>(opts: RetryOptions, fn: (attempt: number) => Promise<T>): Promise<T> => {
    if (!(Number.isInteger(opts.attempts) && opts.attempts >= 1)) throw new Error("retry: attempts must be a positive integer");
    let delay = Math.max(0, opts.delaySeconds) * 1000;
    for (let attempt = 1; ; attempt++) {
      try {
        return await fn(attempt);
      } catch (e) {
        if (attempt >= opts.attempts || signal.aborted) throw e;
        log("retrying", { attempt, of: opts.attempts, error: (e as Error)?.message ?? String(e) });
        await new Promise<void>((res, rej) => {
          const t = setTimeout(() => {
            signal.removeEventListener("abort", onAbort);
            res();
          }, delay);
          const onAbort = () => {
            clearTimeout(t);
            rej(new Error("retry cancelled: the step was cancelled"));
          };
          signal.addEventListener("abort", onAbort, { once: true });
        });
        delay *= opts.backoff ?? 1;
      }
    }
  };
}

/** The real ctx handed to handlers by the harness (and by testHandler). */
export function createCtx(o: CtxOptions): Ctx {
  mkdirSync(o.paths.tmp, { recursive: true });
  mkdirSync(o.paths.workFiles, { recursive: true });
  const sink = o.logSink ?? ((line: string) => process.stderr.write(line + "\n"));
  const log = (message: string, fields?: Record<string, unknown>) => sink(formatLog(message, fields));
  const { sh, exec } = createSh({ tmpDir: o.paths.tmp, signal: o.signal });
  const filesDeps = { run: o.run, workFiles: o.paths.workFiles, tmpDir: o.paths.tmp, root: o.paths.root, signal: o.signal, log };
  return {
    secrets: createSecrets(o.secrets, o.secretValues) as Ctx["secrets"],
    sh,
    exec,
    readText: async (p) => readFileSync(resolve(p), "utf8"),
    readJson: async <T>(p: string) => JSON.parse(readFileSync(resolve(p), "utf8")) as T,
    files: { fetch: createFetch(filesDeps) },
    output: createOutput(filesDeps),
    http: createHttp(o.signal),
    retry: createRetry(o.signal, log),
    log,
    signal: o.signal,
    run: Object.freeze({ ...o.run }),
    tmpDir: o.paths.tmp,
    skip: skipped,
  };
}
