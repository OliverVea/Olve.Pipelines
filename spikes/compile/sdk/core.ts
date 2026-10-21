// Minimal runtime SDK for the compile spike. Types are deliberately loose;
// the type-level builder is prototyped separately in spikes/sdk-types.
import { z } from "zod";

export { z };

// ---------- models ----------
export interface Model<S extends z.ZodType = z.ZodType> {
  readonly kind: "model";
  readonly name: string;
  readonly schema: S;
}

export function model<S extends z.ZodType>(name: string, schema: S): Model<S> {
  return Object.freeze({ kind: "model" as const, name, schema });
}

/** A file in the step's bundle dir, referenced from structured output. */
export function fileRef() {
  return z.object({ path: z.string() }).meta({ "x-olve-fileRef": true });
}

export interface OptionalModel { readonly kind: "optional"; readonly model: Model }
export function optional(m: Model): OptionalModel {
  return { kind: "optional", model: m };
}
export type InputSpec = Model | OptionalModel;
export const modelOf = (s: InputSpec): Model => (s.kind === "optional" ? s.model : s);

// ---------- secrets ----------
export interface Secret { readonly kind: "secret"; readonly name: string; readonly description: string }
export function secret(name: string, description: string): Secret {
  return { kind: "secret", name, description };
}

// ---------- steps ----------
export interface StepDef {
  name: string;
  image: string;
  /** Models, optional(Model), or inline zod schemas (wrapped as `<step>.<key>` models). */
  input: Record<string, InputSpec | z.ZodType>;
  output: Model;
  secrets?: Secret[];
  timeoutSeconds?: number;
}
export interface Step extends Omit<StepDef, "input"> { readonly kind: "step" | "wait"; input: Record<string, InputSpec> }

function normalizeInputs(name: string, input: StepDef["input"]): Record<string, InputSpec> {
  return Object.fromEntries(Object.entries(input).map(([k, v]) =>
    [k, v instanceof z.ZodType ? model(`${name}.${k}`, v) : (v as InputSpec)]));
}

export function step(def: StepDef): Step {
  return { ...def, input: normalizeInputs(def.name, def.input), kind: "step" };
}

// Wait step (library step). Only the scheduled `check` resolver is implemented here.
export interface WaitDef extends Omit<StepDef, "image"> {
  image?: string;
  resolveBy: {
    manual?: boolean;
    callback?: boolean;
    check?: { every: string; run: (input: any, ctx: Ctx) => Promise<unknown> | unknown };
  };
  timeoutHours?: number;
}
export interface WaitStep extends Step { readonly kind: "wait"; resolveBy: WaitDef["resolveBy"]; timeoutHours?: number }
export const steps = {
  wait(def: WaitDef): WaitStep {
    return { image: "olve-harness", ...def, input: normalizeInputs(def.name, def.input), kind: "wait" } as WaitStep;
  },
};

// ---------- handler results ----------
const SKIPPED = Symbol.for("olve.skipped");
const NOT_YET = Symbol.for("olve.notYet");
export interface Skipped { readonly [SKIPPED]: true; readonly reason: string }
export interface NotYet { readonly [NOT_YET]: true }
export const skip = (reason: string): Skipped => ({ [SKIPPED]: true, reason });
export const notYet = (): NotYet => ({ [NOT_YET]: true });
export const isSkipped = (v: unknown): v is Skipped => typeof v === "object" && v !== null && SKIPPED in v;
export const isNotYet = (v: unknown): v is NotYet => typeof v === "object" && v !== null && NOT_YET in v;

// ---------- ctx (implemented in ctx.ts; declared here to avoid a cycle) ----------
export interface ShOptions { env?: Record<string, string>; args?: string[]; cwd?: string }
export interface Ctx {
  sh: ((cmdOrScript: string | { script: string }, opts?: ShOptions) => Promise<void>) & {
    text: (cmdOrScript: string | { script: string }, opts?: ShOptions) => Promise<string>;
  };
  readText(path: string): Promise<string>;
  readJson<T = unknown>(path: string): Promise<T>;
  output: { file(name: string, content: string | Uint8Array): Promise<{ path: string }> };
  log(msg: string): void;
  skip: typeof skip;
  notYet: typeof notYet;
  run: { id: string; number: number; version: string };
}

// ---------- handlers ----------
export interface Handler {
  readonly kind: "handler";
  readonly step: Step;
  readonly fn: (input: any, ctx: Ctx) => Promise<unknown> | unknown;
}
export function implement<S extends Step>(
  s: S,
  fn: (input: any, ctx: Ctx) => Promise<unknown> | unknown,
): Handler {
  return { kind: "handler", step: s, fn };
}

// ---------- pipeline builder ----------
export type Mapper = (r: any) => unknown;
export interface StageStep { step: Step; mapper?: Mapper }
export type Node =
  | { type: "stage"; steps: StageStep[] }
  | { type: "when"; cond: Record<string, unknown>; body: Node[] };

export type StageArg = Step | [Step, Mapper?];

export class Builder {
  readonly nodes: Node[] = [];

  /** .stage(step) | .stage(step, mapper) | .stage([a, mapA], [b, mapB], c) */
  stage(step: Step, mapper?: Mapper): this;
  stage(...entries: StageArg[]): this;
  stage(...args: unknown[]): this {
    let entries: StageStep[];
    if (args.length === 2 && typeof args[1] === "function" && !Array.isArray(args[0])) {
      entries = [{ step: args[0] as Step, mapper: args[1] as Mapper }];
    } else {
      entries = (args as StageArg[]).map((a) =>
        Array.isArray(a) ? { step: a[0], mapper: a[1] } : { step: a },
      );
    }
    this.nodes.push({ type: "stage", steps: entries });
    return this;
  }

  when(cond: Record<string, unknown>, body: (p: Builder) => Builder): this {
    const sub = body(new Builder());
    this.nodes.push({ type: "when", cond, body: sub.nodes });
    return this;
  }
}

export interface Trigger { kind: "push" | "poll"; mapper: (event: any) => unknown; config?: Record<string, unknown> }

export class PipelineBuilder extends Builder {
  readonly triggers: Trigger[] = [];
  constructor(readonly def: { name: string; description?: string; input: Model }) {
    super();
  }
  onPush(mapper: (e: any) => unknown): this {
    this.triggers.push({ kind: "push", mapper });
    return this;
  }
  onPoll(config: { url: string; headers?: Record<string, string>; valuePath: string; intervalSeconds: number },
         mapper: (value: unknown) => unknown): this {
    this.triggers.push({ kind: "poll", mapper, config });
    return this;
  }
}

export function pipeline(def: { name: string; description?: string; input: Model }): PipelineBuilder {
  return new PipelineBuilder(def);
}
