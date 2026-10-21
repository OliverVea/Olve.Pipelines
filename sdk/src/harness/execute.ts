// The shared execution path: validate input → handler → validate output → envelope.
// Used by the compiled harness (`run`), the dev runner and testHandler.
import { existsSync } from "node:fs";
import { join } from "node:path";
import { isSkipped } from "../core/handlers.ts";
import type { AnyStep } from "../core/steps.ts";
import type { Ctx, RunInfo } from "../ctx/types.ts";
import { objectPath } from "../ctx/files.ts";
import { sha256File } from "../util/hash.ts";
import { EXIT } from "./exit.ts";
import { validateInput, validateOutput } from "./validate.ts";

export type FailureKind = "handler" | "input" | "output";
export type Envelope =
  | { status: "succeeded"; output: unknown }
  | { status: "skipped"; reason: string }
  | { status: "failed"; error: { kind: FailureKind; message: string } };

export interface Execution {
  envelope: Envelope;
  exitCode: number;
}

export type StepFunction = (input: Record<string, unknown>, ctx: Ctx) => unknown;

/** Signal → exit code, when the handler failed after a cancellation. */
export function cancelExitCode(signal: AbortSignal): number | undefined {
  if (!signal.aborted) return undefined;
  return signal.reason === "SIGINT" ? EXIT.sigint : EXIT.sigterm;
}

const errorMessage = (e: unknown): string => (e instanceof Error ? e.message : String(e));

/** File refs in `value` that claim to be this attempt's own outputs must exist with the right hash. */
function checkOwnFileRefs(value: unknown, run: RunInfo, workFiles: string): string[] {
  const problems: string[] = [];
  const walk = (v: unknown, at: string) => {
    if (Array.isArray(v)) return v.forEach((x, i) => walk(x, `${at}[${i}]`));
    if (typeof v !== "object" || v === null) return;
    const o = v as Record<string, unknown>;
    if (typeof o.path === "string" && typeof o.sha256 === "string" && o.run === run.number && o.step === run.step && o.attempt === run.attempt) {
      const file = join(workFiles, objectPath(o.path));
      if (!existsSync(file)) problems.push(`${at}: file reference '${o.path}' points to this attempt but no such output file was produced`);
      else if (sha256File(file) !== o.sha256) problems.push(`${at}: file reference '${o.path}' has the wrong sha256 (the file changed after it was referenced)`);
      return;
    }
    for (const [k, x] of Object.entries(o)) walk(x, `${at}.${k}`);
  };
  walk(value, "output");
  return problems;
}

export async function executeStep(args: {
  step: AnyStep;
  fn: StepFunction;
  rawInput: unknown;
  ctx: Ctx;
  workFiles: string;
  log: (line: string) => void;
}): Promise<Execution> {
  const { step, ctx, log } = args;
  const input = validateInput(step, args.rawInput);
  if (!input.ok) {
    const message = `step '${step.name}': input does not match the step's inputs:\n  ${input.issues.join("\n  ")}`;
    log(message);
    return { envelope: { status: "failed", error: { kind: "input", message } }, exitCode: EXIT.inputInvalid };
  }

  let value: unknown;
  try {
    value = await args.fn(input.value, ctx);
  } catch (e) {
    const cancelled = cancelExitCode(ctx.signal);
    const message = cancelled
      ? `step '${step.name}' was cancelled (${String(ctx.signal.reason)}): ${errorMessage(e)}`
      : `step '${step.name}' failed: ${errorMessage(e)}`;
    log(message);
    if (e instanceof Error && e.stack && !cancelled) log(e.stack);
    return { envelope: { status: "failed", error: { kind: "handler", message } }, exitCode: cancelled ?? EXIT.handlerFailed };
  }

  if (isSkipped(value)) {
    log(`step '${step.name}' skipped: ${value.reason}`);
    return { envelope: { status: "skipped", reason: value.reason }, exitCode: EXIT.ok };
  }
  const out = validateOutput(step, value);
  const problems = out.ok ? checkOwnFileRefs(out.value, ctx.run, args.workFiles) : out.issues;
  if (!out.ok || problems.length) {
    const message = `step '${step.name}': returned value does not match ${step.output?.name ?? "its (absent) output model"}:\n  ${problems.join("\n  ")}`;
    log(message);
    return { envelope: { status: "failed", error: { kind: "output", message } }, exitCode: EXIT.outputInvalid };
  }
  return { envelope: { status: "succeeded", output: out.value }, exitCode: EXIT.ok };
}
