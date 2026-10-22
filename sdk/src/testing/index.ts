// @olivervea/pipelines/testing — run a handler under `bun test` through the real harness path
// (validate input → handler → strict output validation), with a real ctx you can override.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { isImplementation, type Implementation } from "../core/handlers.ts";
import type { FileRef } from "../core/models.ts";
import { isStep, type AnyStep, type LibraryStep, type OutputValue, type SecretsOf, type StepInput } from "../core/steps.ts";
import { createCtx } from "../ctx/index.ts";
import type { Ctx, RunInfo } from "../ctx/types.ts";
import { executeStep, type FailureKind, type StepFunction } from "../harness/execute.ts";
import { sha256Hex } from "../util/hash.ts";

export interface TestHandlerOptions<S extends AnyStep> {
  /** Replace parts of ctx (e.g. `sh`, `http`, `files`). The rest is the real implementation. */
  ctx?: Partial<Ctx<SecretsOf<S>>>;
  /** Secret values (instead of reading the environment). */
  secrets?: Partial<Record<SecretsOf<S>, string>>;
  run?: Partial<Omit<RunInfo, "step">>;
  /** Directory used as $OLVE_DIR (default: a fresh temp dir). */
  dir?: string;
  /** Abort signal handed to ctx.signal. */
  signal?: AbortSignal;
}

export type TestResult<S extends AnyStep> = {
  /** $OLVE_DIR used for the run; output files are under `<dir>/work/files`. */
  dir: string;
  filesDir: string;
  logs: string[];
  exitCode: number;
} & (
  | { status: "succeeded"; output: OutputValue<S> }
  | { status: "skipped"; reason: string }
  | { status: "failed"; error: { kind: FailureKind; message: string } }
);

/**
 * Runs a handler (the default export of `handlers/<step>.ts`) or a library step with `input`.
 * Input is validated like `input.json`, the result like a real run: an invalid output gives
 * `{ status: "failed", error: { kind: "output" } }`, a throwing handler `kind: "handler"`.
 */
export async function testHandler<S extends AnyStep>(
  target: Implementation<S> | (S & LibraryStep),
  input: StepInput<S["input"]>,
  opts: TestHandlerOptions<S> = {},
): Promise<TestResult<S>> {
  let step: AnyStep;
  let fn: StepFunction;
  if (isImplementation(target)) {
    step = target.step;
    fn = target.handler as unknown as StepFunction;
  } else if (isStep(target) && target.builtin) {
    step = target;
    fn = target.builtin.run as StepFunction;
  } else throw new TypeError("testHandler expects implement(...) (a handler module's default export) or a library step");

  const dir = opts.dir ?? mkdtempSync(join(tmpdir(), `olve-test-${step.name}-`));
  const filesDir = join(dir, "work", "files");
  const logs: string[] = [];
  const run: RunInfo = { pipeline: "test", id: "test", number: 1, attempt: 1, version: "0.0.0-test", ...opts.run, step: step.name };
  const real = createCtx({
    run,
    secrets: step.secrets,
    secretValues: opts.secrets as Record<string, string> | undefined,
    paths: { root: dir, workFiles: filesDir, tmp: join(dir, "tmp") },
    signal: opts.signal ?? new AbortController().signal,
    logSink: (l) => logs.push(l),
  });
  const ctx = { ...real, ...opts.ctx } as Ctx;
  // Round-trip through JSON like input.json does (drops undefined, rejects non-JSON values early).
  const raw = JSON.parse(JSON.stringify(input ?? {}));
  const exec = await executeStep({ step, fn, rawInput: raw, ctx, workFiles: filesDir, log: (l) => logs.push(l) });
  return { dir, filesDir, logs, exitCode: exec.exitCode, ...exec.envelope } as TestResult<S>;
}

/** A file reference for test inputs (`sha256` defaults to the hash of `content`). */
export function testFileRef(ref: { step: string; path: string; run?: number; attempt?: number; sha256?: string; content?: string | Uint8Array }): FileRef {
  return {
    run: ref.run ?? 1,
    step: ref.step,
    attempt: ref.attempt ?? 1,
    path: ref.path,
    sha256: ref.sha256 ?? sha256Hex(ref.content ?? ""),
  } as FileRef;
}
