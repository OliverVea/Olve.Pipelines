// The harness: one executable, three modes.
//   describe [--sentinels]   → manifest (canonical JSON) on stdout
//   run <step>               → $OLVE_DIR/input.json → handler → $OLVE_DIR/work/output.json (+ result upload)
//   map-trigger <trigger>    → $OLVE_DIR/input.json (event) → pipeline input → $OLVE_DIR/work/output.json
import { mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { isImplementation } from "../core/handlers.ts";
import { configRefPath, isConfigRef, unionAt } from "../core/config.ts";
import type { AnyStep } from "../core/steps.ts";
import type { WebhookEvent } from "../core/webhooks.ts";
import { pipelineData, type PipelineData, type StageNode } from "../builder/pipeline.ts";
import { createCtx, formatLog } from "../ctx/index.ts";
import { DescribeError, describePipeline, manifestText } from "../describe/describe.ts";
import type { HarnessInfo } from "../util/handler-hash.ts";
import { ENV, paths, runInfoFromEnv } from "./env.ts";
import { EXIT } from "./exit.ts";
import { executeStep, type Envelope, type Execution, type StepFunction } from "./execute.ts";
import { MANIFEST_BEGIN, MANIFEST_END, packResult, removeStale, sentinelBlock, uploadFile, writeEnvelope } from "./result.ts";
import { formatIssues, parseStrict } from "./validate.ts";

/** step name → lazy import of `handlers/<name>.ts`. Literal import() paths so Bun bundles them. */
export type HandlerRegistry = Readonly<Record<string, () => Promise<{ default: unknown }>>>;

export interface HarnessConfig {
  /** Default export of pipeline.ts. */
  pipeline: unknown;
  handlers: HandlerRegistry;
  handlerHashes: Readonly<Record<string, string>>;
  /** Kind → hash of the code of the steps of that kind that carry their implementation. */
  kindHashes?: Readonly<Record<string, string>>;
  harness: HarnessInfo;
}

export interface HarnessIO {
  stdout(text: string): Promise<void>;
  stderr(line: string): void;
}

export const defaultIO: HarnessIO = {
  stdout: async (text) => {
    await Bun.write(Bun.stdout, text);
  },
  stderr: (line) => {
    process.stderr.write(line.endsWith("\n") ? line : line + "\n");
  },
};

const USAGE = "usage: step describe [--sentinels] | step run <step> | step map-trigger <trigger>";

export function allSteps(data: PipelineData): AnyStep[] {
  const out: AnyStep[] = [];
  for (const { step } of stepsWithCases(data)) if (!out.includes(step)) out.push(step);
  return out;
}

/** Every step with the `select` case conditions (`<ref>.<discriminator>=<value>`) it sits in. */
export function stepsWithCases(data: PipelineData): { step: AnyStep; cases: string[] }[] {
  const out: { step: AnyStep; cases: string[] }[] = [];
  const walk = (nodes: readonly StageNode[], cases: string[]) => {
    for (const n of nodes) {
      if (n.kind === "stage") out.push(...n.steps.map((s) => ({ step: s.step, cases })));
      else if (n.kind === "when") walk(n.nodes, cases);
      else {
        const union = isConfigRef(n.on) ? unionAt(n.on) : undefined;
        for (const [value, inner] of Object.entries(n.cases))
          walk(inner, union ? [...cases, `${configRefPath(n.on)}.${union.discriminator}=${value}`] : cases);
      }
    }
  };
  walk(data.nodes, []);
  for (const fh of data.failureHandlers) out.push({ step: fh.step, cases: [] });
  return out;
}

/** The step `run <name>` executes: the only one so named, or the one in the cases the service selected (`OLVE_CASES`). */
export function findStep(data: PipelineData, name: string, selected: readonly string[]): AnyStep | string {
  const candidates = stepsWithCases(data).filter((c) => c.step.name === name);
  const distinct = candidates.filter((c, i) => candidates.findIndex((d) => d.step === c.step) === i);
  if (distinct.length === 0) return `unknown step '${name}'`;
  if (distinct.length === 1) return distinct[0]!.step;
  const chosen = distinct.filter((c) => c.cases.every((k) => selected.includes(k)));
  if (chosen.length === 1) return chosen[0]!.step;
  return `step '${name}' is in several select cases; ${ENV_CASES} must name the selected one (got ${JSON.stringify(selected)})`;
}

/** The select case conditions of the step being run (JSON array), set by the service for steps inside a select. */
export const ENV_CASES = "OLVE_CASES";

/** Installs SIGTERM/SIGINT handlers that abort the returned signal (PID 1 ignores signals without them). */
export function cancellation(): { signal: AbortSignal; dispose(): void } {
  const ac = new AbortController();
  const onTerm = () => ac.abort("SIGTERM");
  const onInt = () => (ac.signal.aborted ? process.exit(EXIT.sigint) : ac.abort("SIGINT"));
  process.on("SIGTERM", onTerm);
  process.on("SIGINT", onInt);
  return {
    signal: ac.signal,
    dispose() {
      process.off("SIGTERM", onTerm);
      process.off("SIGINT", onInt);
    },
  };
}

/** Resolves the function that implements `step` (carried by the step, or a handler module). */
export async function resolveStepFunction(step: AnyStep, handlers: HandlerRegistry): Promise<StepFunction | string> {
  if (isImplementation(step)) return step.handler as unknown as StepFunction;
  const load = handlers[step.name];
  if (!load) return `step '${step.name}' has no handler`;
  let mod: { default: unknown };
  try {
    mod = await load();
  } catch (e) {
    return `the handler of step '${step.name}' failed to load: ${(e as Error)?.stack ?? String(e)}`;
  }
  const impl = mod.default;
  if (!isImplementation(impl)) return `the handler of step '${step.name}' must default-export implement(<step '${step.name}'>, …)`;
  if (impl.step !== step)
    return `the handler of step '${step.name}' implements a different step object (it must be implement(<the '${step.name}' step from pipeline.ts>, …))`;
  return impl.handler as unknown as StepFunction;
}

/** Writes output.json, prints the sentinel block and uploads result.tar.gz when OLVE_RESULT_URL is set. */
export async function finish(exec: Execution, io: HarnessIO): Promise<number> {
  const p = paths();
  writeEnvelope(p.work, exec.envelope);
  await io.stdout(sentinelBlock(exec.envelope));
  const url = process.env[ENV.resultUrl];
  if (!url) return exec.exitCode;
  try {
    mkdirSync(p.tmp, { recursive: true });
    const archive = join(p.tmp, "result.tar.gz");
    await packResult(p.work, archive);
    await uploadFile(url, archive, io.stderr);
  } catch (e) {
    io.stderr(`result upload failed: ${(e as Error).message}`);
    return exec.exitCode === EXIT.ok ? EXIT.uploadFailed : exec.exitCode;
  }
  return exec.exitCode;
}

function readJson(path: string): { ok: true; value: unknown } | { ok: false; message: string } {
  try {
    return { ok: true, value: JSON.parse(readFileSync(path, "utf8")) };
  } catch (e) {
    return { ok: false, message: `cannot read ${path}: ${(e as Error).message}` };
  }
}

async function describeMode(cfg: HarnessConfig, args: string[], io: HarnessIO): Promise<number> {
  const sentinels = args.includes("--sentinels");
  try {
    const text = manifestText(describePipeline(cfg.pipeline, { handlerNames: Object.keys(cfg.handlers), handlerHashes: cfg.handlerHashes, kindHashes: cfg.kindHashes, harness: cfg.harness }));
    await io.stdout(sentinels ? `${MANIFEST_BEGIN}\n${text}${MANIFEST_END}\n` : text);
    return EXIT.ok;
  } catch (e) {
    if (e instanceof DescribeError) {
      io.stderr(e.message);
      return EXIT.configInvalid;
    }
    throw e;
  }
}

async function runMode(cfg: HarnessConfig, name: string | undefined, io: HarnessIO): Promise<number> {
  if (!name) return io.stderr(USAGE), EXIT.usage;
  const data = pipelineData(cfg.pipeline);
  const step = findStep(data, name, selectedCases());
  if (typeof step === "string") return io.stderr(step), EXIT.usage;
  const fn = await resolveStepFunction(step, cfg.handlers);
  if (typeof fn === "string") return io.stderr(fn), EXIT.configInvalid;

  const p = paths();
  removeStale(p.work);
  mkdirSync(p.files, { recursive: true });
  mkdirSync(p.tmp, { recursive: true });
  const raw = readJson(p.input);
  const cancel = cancellation();
  try {
    let exec: Execution;
    if (!raw.ok) {
      io.stderr(raw.message);
      exec = { envelope: { status: "failed", error: { kind: "input", message: raw.message } }, exitCode: EXIT.inputInvalid };
    } else {
      const run = runInfoFromEnv(step.name);
      const ctx = createCtx({ run, secrets: step.secrets, paths: { root: p.root, workFiles: p.files, tmp: p.tmp }, signal: cancel.signal, logSink: io.stderr });
      exec = await executeStep({ step, fn, rawInput: raw.value, ctx, workFiles: p.files, log: io.stderr });
    }
    return await finish(exec, io);
  } finally {
    cancel.dispose();
  }
}

function isWebhookEvent(v: unknown): boolean {
  if (typeof v !== "object" || v === null || Array.isArray(v) || !("body" in v)) return false;
  const headers = (v as { headers?: unknown }).headers;
  return typeof headers === "object" && headers !== null && !Array.isArray(headers);
}

async function mapTriggerMode(cfg: HarnessConfig, name: string | undefined, io: HarnessIO): Promise<number> {
  if (!name) return io.stderr(USAGE), EXIT.usage;
  const data = pipelineData(cfg.pipeline);
  const trigger = data.triggers.find((t) => t.name === name);
  if (!trigger) return io.stderr(`unknown trigger '${name}' (known: ${data.triggers.map((t) => t.name).join(", ") || "none"})`), EXIT.usage;
  const p = paths();
  removeStale(p.work);
  mkdirSync(p.work, { recursive: true });
  const raw = readJson(p.input);
  const fail = (kind: "input" | "output" | "handler", message: string, exitCode: number): Execution => {
    io.stderr(message);
    return { envelope: { status: "failed", error: { kind, message } }, exitCode };
  };
  let exec: Execution;
  if (!raw.ok) exec = fail("input", raw.message, EXIT.inputInvalid);
  else if (trigger.kind === "poll" && (typeof raw.value !== "object" || raw.value === null || typeof (raw.value as { value?: unknown }).value !== "string"))
    exec = fail("input", `poll trigger '${name}': the event must be {"value": "<string>"}`, EXIT.inputInvalid);
  else if (trigger.kind === "push" && (typeof raw.value !== "object" || raw.value === null || Array.isArray(raw.value)))
    exec = fail("input", `push trigger: the event must be the webhook payload object`, EXIT.inputInvalid);
  else if (trigger.kind === "webhook" && !isWebhookEvent(raw.value))
    exec = fail("input", `webhook trigger '${name}': the event must be {"headers": {...}, "body": ...}`, EXIT.inputInvalid);
  else {
    let mapped: unknown;
    let threw: unknown = undefined;
    try {
      mapped =
        trigger.kind === "poll" ? await trigger.map((raw.value as { value: string }).value)
        : trigger.kind === "webhook" ? await trigger.map(raw.value as WebhookEvent)
        : await trigger.map(raw.value);
    } catch (e) {
      threw = e ?? new Error("undefined");
    }
    if (threw !== undefined) exec = fail("handler", `trigger '${name}' mapper threw: ${(threw as Error)?.message ?? String(threw)}`, EXIT.handlerFailed);
    else if (mapped === null && trigger.kind === "webhook") exec = { envelope: { status: "skipped", reason: `trigger '${name}' ignored the delivery` }, exitCode: EXIT.ok };
    else {
      const r = parseStrict(data.input.schema, mapped, "input");
      exec = r.ok
        ? { envelope: { status: "succeeded", output: r.value } satisfies Envelope, exitCode: EXIT.ok }
        : fail("output", `trigger '${name}': mapped input does not match ${data.input.name}:\n  ${r.issues.join("\n  ")}`, EXIT.outputInvalid);
    }
  }
  return finish(exec, io);
}

/** Entry point of the compiled binary (and the dev runner). Returns the exit code. */
export async function runHarness(cfg: HarnessConfig, argv: readonly string[], io: HarnessIO = defaultIO): Promise<number> {
  const [mode, ...rest] = argv;
  switch (mode) {
    case "describe":
      return describeMode(cfg, rest, io);
    case "run":
      return runMode(cfg, rest[0], io);
    case "map-trigger":
      return mapTriggerMode(cfg, rest[0], io);
    default:
      io.stderr(USAGE);
      return EXIT.usage;
  }
}

export { formatIssues, formatLog };

function selectedCases(): string[] {
  try {
    const v = JSON.parse(process.env[ENV_CASES] ?? "[]") as unknown;
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];
  } catch {
    return [];
  }
}
