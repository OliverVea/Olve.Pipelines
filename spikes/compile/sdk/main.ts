// The harness: one executable, many modes.
//   describe                 → manifest JSON on stdout
//   run <step>               → $OLVE_DIR/input.json → handler → $OLVE_DIR/output.json
//   check <step>             → wait-step check → $OLVE_DIR/output.json (resolved | notYet)
//   map-trigger <trigger>    → $OLVE_DIR/event.json → pipeline input JSON on stdout
import { z } from "zod";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { type Handler, type PipelineBuilder, type Step, type WaitStep, isNotYet, isSkipped, modelOf } from "./core";
import { createCtx } from "./ctx";
import { describe, DescribeError } from "./describe";
import { canonicalJson } from "./canonical";

export interface HarnessConfig {
  pipeline: PipelineBuilder;
  /** step name → lazy import of the handler module. Literal import() paths so Bun bundles them. */
  handlers: Record<string, () => Promise<{ default: Handler }>>;
  handlerHashes: Record<string, string>;
}

const EXIT = { ok: 0, usage: 64, handlerFailed: 1, inputInvalid: 65, outputInvalid: 66, config: 78 } as const;

function die(code: number, msg: string): never {
  console.error(msg);
  process.exit(code);
}

function formatIssues(err: z.ZodError, prefix: string): string {
  return err.issues.map((i) => `  ${prefix}${i.path.length ? "." + i.path.join(".") : ""}: ${i.message}`).join("\n");
}

function allSteps(p: PipelineBuilder): Step[] {
  const out: Step[] = [];
  const walk = (nodes: PipelineBuilder["nodes"]) => {
    for (const n of nodes) n.type === "stage" ? out.push(...n.steps.map((s) => s.step)) : walk(n.body);
  };
  walk(p.nodes);
  return out;
}

async function readInput(s: Step, olveDir: string) {
  const path = join(olveDir, "input.json");
  const raw = await Bun.file(path).json().catch((e) => die(EXIT.inputInvalid, `cannot read ${path}: ${e.message}`));
  const issues: string[] = [];
  const parsed: Record<string, unknown> = {};
  for (const [k, spec] of Object.entries(s.input)) {
    if (raw[k] === undefined && spec.kind === "optional") continue;
    const r = modelOf(spec).schema.safeParse(raw[k]);
    if (r.success) parsed[k] = r.data;
    else issues.push(formatIssues(r.error, `input.${k}`));
  }
  if (issues.length) die(EXIT.inputInvalid, `step '${s.name}': input does not match its models:\n${issues.join("\n")}`);
  return parsed;
}

function writeResult(olveDir: string, result: unknown) {
  mkdirSync(olveDir, { recursive: true });
  writeFileSync(join(olveDir, "output.json"), canonicalJson(result));
}

function validateOutput(s: Step, value: unknown) {
  const r = s.output.schema.safeParse(value);
  if (!r.success)
    die(EXIT.outputInvalid, `step '${s.name}': returned value does not match ${s.output.name}:\n${formatIssues(r.error, "output")}`);
  return r.data;
}

export async function harness(cfg: HarnessConfig, argv = process.argv.slice(2)) {
  const [mode, arg] = argv;
  const olveDir = process.env.OLVE_DIR ?? "/olve";
  const workDir = process.env.OLVE_WORKDIR ?? process.cwd();
  const findStep = (name: string | undefined) => {
    if (!name) die(EXIT.usage, `usage: ${mode} <step>`);
    return allSteps(cfg.pipeline).find((s) => s.name === name) ?? die(EXIT.usage, `unknown step '${name}'`);
  };

  switch (mode) {
    case "describe": {
      try {
        const m = describe(cfg.pipeline, { handlerHashes: cfg.handlerHashes, handlerSteps: Object.keys(cfg.handlers) });
        process.stdout.write(canonicalJson(m));
      } catch (e) {
        if (e instanceof DescribeError) die(EXIT.config, e.message);
        throw e;
      }
      return;
    }
    case "run": {
      const s = findStep(arg);
      if (s.kind !== "step") die(EXIT.usage, `step '${s.name}' is a ${s.kind} step; use 'check'`);
      const load = cfg.handlers[s.name] ?? die(EXIT.config, `no handler for step '${s.name}'`);
      const input = await readInput(s, olveDir);
      const handler = (await load()).default;
      if (handler?.kind !== "handler" || handler.step !== s)
        die(EXIT.config, `handlers/${s.name}.ts must default-export implement(<step '${s.name}'>, …)`);
      let value: unknown;
      try {
        value = await handler.fn(input, createCtx({ olveDir, workDir }));
      } catch (e: any) {
        die(EXIT.handlerFailed, `step '${s.name}' failed: ${e?.message ?? e}`);
      }
      if (isSkipped(value)) {
        writeResult(olveDir, { status: "skipped", reason: value.reason });
        console.error(`step '${s.name}' skipped: ${value.reason}`);
        return;
      }
      writeResult(olveDir, { status: "succeeded", output: validateOutput(s, value) });
      return;
    }
    case "check": {
      const s = findStep(arg) as WaitStep;
      const check = s.kind === "wait" ? s.resolveBy.check : undefined;
      if (!check) die(EXIT.usage, `step '${s.name}' has no check resolver`);
      const input = await readInput(s, olveDir);
      const value = await check.run(input, createCtx({ olveDir, workDir }));
      if (isNotYet(value)) writeResult(olveDir, { status: "notYet" });
      else writeResult(olveDir, { status: "resolved", output: validateOutput(s, value) });
      return;
    }
    case "map-trigger": {
      const t = cfg.pipeline.triggers.find((x) => x.kind === arg) ?? die(EXIT.usage, `unknown trigger '${arg}'`);
      const path = process.env.OLVE_EVENT ?? join(olveDir, "event.json");
      const event = await Bun.file(path).json().catch((e) => die(EXIT.inputInvalid, `cannot read ${path}: ${e.message}`));
      const mapped = await t.mapper(event);
      const model = cfg.pipeline.def.input;
      const r = model.schema.safeParse(mapped);
      if (!r.success) die(EXIT.outputInvalid, `trigger '${arg}': mapped input does not match ${model.name}:\n${formatIssues(r.error, "input")}`);
      process.stdout.write(canonicalJson(r.data));
      return;
    }
    default:
      die(EXIT.usage, "usage: step <describe | run <step> | check <step> | map-trigger <trigger>>");
  }
}
