import { mkdirSync, mkdtempSync } from "node:fs";
import { join } from "node:path";
import type { HarnessIO } from "../../src/harness/main.ts";

export const SDK = join(import.meta.dir, "../..");
export const EXAMPLE = join(SDK, "examples/self-deploy");
export const TMP = join(SDK, "tests/.tmp");
export const SHA = "0123456789abcdef0123456789abcdef01234567";

/** A fresh directory inside the SDK (so `@olve/pipelines` resolves via tsconfig paths). */
export function tmp(prefix: string): string {
  mkdirSync(TMP, { recursive: true });
  return mkdtempSync(join(TMP, `${prefix}-`));
}

/** Runs `fn` with env vars set (undefined = unset), restoring afterwards. */
export async function withEnv<T>(vars: Record<string, string | undefined>, fn: () => Promise<T> | T): Promise<T> {
  const saved: Record<string, string | undefined> = {};
  for (const k of Object.keys(vars)) {
    saved[k] = process.env[k];
    if (vars[k] === undefined) delete process.env[k];
    else process.env[k] = vars[k];
  }
  try {
    return await fn();
  } finally {
    for (const [k, v] of Object.entries(saved)) {
      if (v === undefined) delete process.env[k];
      else process.env[k] = v;
    }
  }
}

export function captureIO(): HarnessIO & { out: string; err: string[] } {
  const io = {
    out: "",
    err: [] as string[],
    async stdout(t: string) {
      io.out += t;
    },
    stderr(l: string) {
      io.err.push(l);
    },
  };
  return io;
}
