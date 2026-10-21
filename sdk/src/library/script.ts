import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Ctx, RunInfo } from "../ctx/types.ts";
import type { AnyModel } from "../core/models.ts";
import type { Secret } from "../core/secrets.ts";
import { makeStep, type InputSpec, type LibraryStep, type Step, type StepDef, type StepInput } from "../core/steps.ts";
import { sha256Hex } from "../util/hash.ts";

/** A config value that may depend on the run: a constant or a function of the step's inputs and run info. */
export type RunValue<I, T = string> = T | ((input: StepInput<I>, run: RunInfo) => T);

export interface ScriptDef<N extends string, I extends InputSpec, O extends AnyModel | null, Sec extends readonly Secret[]>
  extends StepDef<N, I, O, Sec> {
  /** An embedded script: `import s from "./scripts/x.sh" with { type: "file" }`. */
  script: string;
  /** Extra environment; values may be functions of the inputs (evaluated in the step, not at describe). */
  env?: Record<string, RunValue<I>>;
}

/** `deployTo` → `INPUT_DEPLOY_TO`. */
export function inputEnvName(key: string): string {
  return "INPUT_" + key.replace(/([a-z0-9])([A-Z])/g, "$1_$2").replace(/[^A-Za-z0-9]+/g, "_").toUpperCase();
}

/**
 * Runs a shell script (the zero-TypeScript path). Contract for the script:
 *  - `$OLVE_INPUT`: path of the validated input as JSON; each input key is also `INPUT_<KEY>`
 *    (strings raw, everything else JSON).
 *  - `$OLVE_OUTPUT`: if the step has an output model, write the output JSON there. Objects of the
 *    form `{"$file": "<path>"}` / `{"$dir": "<path>"}` are turned into file references.
 *  - Declared secrets are in the environment under their names.
 */
export function script<
  const N extends string,
  const I extends InputSpec = {},
  O extends AnyModel | null = null,
  const Sec extends readonly Secret[] = [],
>(def: ScriptDef<N, I, O, Sec>): Step<N, I, O, Sec[number]["name"]> & LibraryStep {
  const env = def.env ?? {};
  return makeStep("script", def as never, {
    manifestConfig: () => ({
      scriptSha256: "sha256:" + sha256Hex(readFileSync(def.script)),
      env: Object.fromEntries(Object.entries(env).filter(([, v]) => typeof v === "string")),
      computedEnv: Object.keys(env).filter((k) => typeof env[k] === "function").sort(),
    }),
    async run(input: Record<string, unknown>, ctx: Ctx) {
      mkdirSync(ctx.tmpDir, { recursive: true });
      const dir = mkdtempSync(join(ctx.tmpDir, "script-"));
      const inputPath = join(dir, "input.json");
      const outputPath = join(dir, "output.json");
      writeFileSync(inputPath, JSON.stringify(input));
      const vars: Record<string, string> = { OLVE_INPUT: inputPath };
      for (const [k, v] of Object.entries(input)) {
        if (v !== undefined) vars[inputEnvName(k)] = typeof v === "string" ? v : JSON.stringify(v);
      }
      for (const [k, v] of Object.entries(env)) {
        vars[k] = typeof v === "function" ? (v as (i: unknown, r: RunInfo) => string)(input, ctx.run) : v;
      }
      if (def.output) vars.OLVE_OUTPUT = outputPath;
      await ctx.sh({ script: def.script }, { env: vars });
      if (!def.output) return undefined;
      let raw: unknown;
      try {
        raw = JSON.parse(readFileSync(outputPath, "utf8"));
      } catch (e) {
        throw new Error(`script did not write valid JSON to $OLVE_OUTPUT: ${(e as Error).message}`);
      }
      return resolveFilePlaceholders(raw, ctx);
    },
  }) as never;
}

async function resolveFilePlaceholders(v: unknown, ctx: Ctx): Promise<unknown> {
  if (Array.isArray(v)) return Promise.all(v.map((x) => resolveFilePlaceholders(x, ctx)));
  if (typeof v === "object" && v !== null) {
    const o = v as Record<string, unknown>;
    const keys = Object.keys(o);
    if (keys.length === 1 && typeof o.$file === "string") return ctx.output.file(o.$file);
    if (keys.length === 1 && typeof o.$dir === "string") return ctx.output.dir(o.$dir);
    const out: Record<string, unknown> = {};
    for (const k of keys) out[k] = await resolveFilePlaceholders(o[k], ctx);
    return out;
  }
  return v;
}
