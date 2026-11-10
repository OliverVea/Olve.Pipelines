import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step } from '../../src/index.ts';
import type { HarnessIO } from '../../src/harness/main.ts';

export const SDK = join(import.meta.dir, '../..');
export const EXAMPLE = join(SDK, 'examples/self-deploy');
export const TMP = join(SDK, 'tests/.tmp');
export const SHA = '0123456789abcdef0123456789abcdef01234567';

/**
 * A fresh directory inside the SDK (so `@olivervea/pipelines` resolves via tsconfig paths). One made
 * in a test is removed when the test ends, one made in a `beforeAll` when its file ends: builds are
 * ~130 MB each and would otherwise pile up past the build-sdk step's scratch limit.
 */
export function tmp(prefix: string): string {
  mkdirSync(TMP, { recursive: true });
  const dir = mkdtempSync(join(TMP, `${prefix}-`));
  (inTest ? madeInTest : made).push(dir);
  return dir;
}

const made: string[] = [];
const madeInTest: string[] = [];
let inTest = false;

const remove = (dirs: string[]) => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
};

/** `tests/cleanup.ts` calls these around every test. */
export function testStarted(): void {
  inTest = true;
}
export function testEnded(): void {
  inTest = false;
  remove(madeInTest);
}

/** Removes the directories made outside tests; each file that uses `tmp` calls it when it ends. */
export function removeTmpDirs(): void {
  remove(madeInTest);
  remove(made);
}

/** `step()` with a no-op implementation, for tests about anything but the handler. */
export const stubStep = ((def: never) =>
  implement(step(def), (() => undefined) as never)) as unknown as typeof step;

/** Runs `fn` with env vars set (undefined = unset), restoring afterwards. */
export async function withEnv<T>(
  vars: Record<string, string | undefined>,
  fn: () => Promise<T> | T,
): Promise<T> {
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
    out: '',
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
