// The shared execution path: validate input → handler → validate output → envelope.
// Used by the compiled harness (`run`), the dev runner and testHandler.
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { isSkipped } from '../core/handlers.ts';
import { isExternal } from '../core/external.ts';
import type { AnyStep } from '../core/steps.ts';
import type { Ctx, RunInfo } from '../ctx/types.ts';
import { objectPath } from '../ctx/files.ts';
import { createLinks, type StepLink } from '../ctx/links.ts';
import { sha256File } from '../util/hash.ts';
import { EXIT } from './exit.ts';
import { CONFIG_KEY, validateConfig, validateInput, validateOutput } from './validate.ts';

export type FailureKind = 'handler' | 'input' | 'output';
export type Envelope = (
  | { status: 'succeeded'; output: unknown }
  | { status: 'skipped'; reason: string }
  | { status: 'failed'; error: { kind: FailureKind; message: string } }
) & { links?: StepLink[] };

export interface Execution {
  envelope: Envelope;
  exitCode: number;
}

export type StepFunction = (input: Record<string, unknown>, ctx: Ctx) => unknown;

/** Signal → exit code, when the handler failed after a cancellation. */
export function cancelExitCode(signal: AbortSignal): number | undefined {
  if (!signal.aborted) return undefined;
  return signal.reason === 'SIGINT' ? EXIT.sigint : EXIT.sigterm;
}

const errorMessage = (e: unknown): string => (e instanceof Error ? e.message : String(e));

/** File refs in `value` that claim to be this attempt's own outputs must exist with the right hash. */
function checkOwnFileRefs(value: unknown, run: RunInfo, workFiles: string): string[] {
  const problems: string[] = [];
  const walk = (v: unknown, at: string) => {
    if (Array.isArray(v)) return v.forEach((x, i) => walk(x, `${at}[${i}]`));
    if (typeof v !== 'object' || v === null) return;
    const o = v as Record<string, unknown>;
    if (
      typeof o.path === 'string' &&
      typeof o.sha256 === 'string' &&
      o.run === run.number &&
      o.step === run.step &&
      o.attempt === run.attempt
    ) {
      const file = join(workFiles, objectPath(o.path));
      if (!existsSync(file))
        problems.push(
          `${at}: file reference '${o.path}' points to this attempt but no such output file was produced`,
        );
      else if (sha256File(file) !== o.sha256)
        problems.push(
          `${at}: file reference '${o.path}' has the wrong sha256 (the file changed after it was referenced)`,
        );
      return;
    }
    for (const [k, x] of Object.entries(o)) walk(x, `${at}.${k}`);
  };
  walk(value, 'output');
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
  const { step, log } = args;
  const input = validateInput(step, args.rawInput);
  const config = validateConfig(step, args.rawInput);
  if (!input.ok || !config.ok) {
    const issues = [
      ...(input.ok ? [] : input.issues),
      ...(config.ok ? [] : config.issues),
    ];
    const message = `step '${step.name}': input does not match the step's inputs:\n  ${issues.join('\n  ')}`;
    log(message);
    return {
      envelope: { status: 'failed', error: { kind: 'input', message } },
      exitCode: EXIT.inputInvalid,
    };
  }
  const collected = createLinks();
  const ctx: Ctx = {
    ...args.ctx,
    config: Object.freeze(config.value) as Ctx['config'],
    link: collected.link,
  };
  const withLinks = (e: Envelope): Envelope =>
    collected.links.length ? { ...e, links: [...collected.links] } : e;

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
    return {
      envelope: withLinks({ status: 'failed', error: { kind: 'handler', message } }),
      exitCode: cancelled ?? EXIT.handlerFailed,
    };
  }

  if (isSkipped(value)) {
    log(`step '${step.name}' skipped: ${value.reason}`);
    return {
      envelope: withLinks({ status: 'skipped', reason: value.reason }),
      exitCode: EXIT.ok,
    };
  }
  const out = validateOutput(step, value);
  const problems = out.ok
    ? checkOwnFileRefs(out.value, ctx.run, args.workFiles)
    : out.issues;
  if (!out.ok || problems.length) {
    const message = `step '${step.name}': returned value does not match ${step.output?.name ?? 'its (absent) output model'}:\n  ${problems.join('\n  ')}`;
    log(message);
    return {
      envelope: withLinks({ status: 'failed', error: { kind: 'output', message } }),
      exitCode: EXIT.outputInvalid,
    };
  }
  return {
    envelope: withLinks({ status: 'succeeded', output: out.value }),
    exitCode: EXIT.ok,
  };
}

/**
 * A step's preflight: validates the step's non-external inputs (external values already given are
 * ignored), runs the preflight and reports its links. Succeeds with `output: null`; nothing is validated
 * as output and no files are kept.
 */
export async function executePreflight(args: {
  step: AnyStep;
  fn: StepFunction;
  rawInput: unknown;
  ctx: Ctx;
  workFiles: string;
  log: (line: string) => void;
}): Promise<Execution> {
  const { step, log } = args;
  const entries = Object.entries(step.input as Record<string, unknown>);
  const external = new Set(entries.filter(([, e]) => isExternal(e)).map(([k]) => k));
  const regular = {
    ...step,
    input: Object.fromEntries(entries.filter(([k]) => !external.has(k))),
  };
  const raw =
    typeof args.rawInput === 'object' &&
    args.rawInput !== null &&
    !Array.isArray(args.rawInput)
      ? Object.fromEntries(
          Object.entries(args.rawInput).filter(
            ([k]) => k === CONFIG_KEY || !external.has(k),
          ),
        )
      : args.rawInput;
  const input = validateInput(regular, raw);
  const config = validateConfig(step, raw);
  if (!input.ok || !config.ok) {
    const issues = [
      ...(input.ok ? [] : input.issues),
      ...(config.ok ? [] : config.issues),
    ];
    const message = `step '${step.name}' preflight: input does not match the step's inputs:\n  ${issues.join('\n  ')}`;
    log(message);
    return {
      envelope: { status: 'failed', error: { kind: 'input', message } },
      exitCode: EXIT.inputInvalid,
    };
  }
  const collected = createLinks();
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { output: _output, skip: _skip, ...rest } = args.ctx;
  const ctx = {
    ...rest,
    config: Object.freeze(config.value) as Ctx['config'],
    link: collected.link,
  };
  const withLinks = (e: Envelope): Envelope =>
    collected.links.length ? { ...e, links: [...collected.links] } : e;
  try {
    await args.fn(input.value, ctx as Ctx);
  } catch (e) {
    const cancelled = cancelExitCode(ctx.signal);
    const message = cancelled
      ? `step '${step.name}' preflight was cancelled (${String(ctx.signal.reason)}): ${errorMessage(e)}`
      : `step '${step.name}' preflight failed: ${errorMessage(e)}`;
    log(message);
    if (e instanceof Error && e.stack && !cancelled) log(e.stack);
    return {
      envelope: withLinks({ status: 'failed', error: { kind: 'handler', message } }),
      exitCode: cancelled ?? EXIT.handlerFailed,
    };
  }
  log(`step '${step.name}' preflight succeeded`);
  return {
    envelope: withLinks({ status: 'succeeded', output: null }),
    exitCode: EXIT.ok,
  };
}
