import type { z } from "zod";
import { STEP, type StepBrand } from "./brands.ts";
import type { AnyModel, Infer, Optional } from "./models.ts";
import type { AnyConfigEntry } from "./config.ts";
import { configRefsIn } from "./config-refs.ts";
import type { Secret } from "./secrets.ts";
import type { Simplify } from "./types.ts";

/** A step input entry: a model (matchable by model), `optional(model)`, or a plain zod schema (mapper required). */
export type InputEntry = AnyModel | Optional | z.ZodType;
export type InputSpec = { readonly [key: string]: InputEntry };

export type InferEntry<E> =
  E extends Optional<infer M> ? Infer<M> | undefined
  : E extends AnyModel ? Infer<E>
  : E extends z.ZodType ? z.output<E>
  : never;
/** What a handler receives: the output view of every input entry. */
export type StepInput<I> = Simplify<{ -readonly [K in keyof I]: InferEntry<I[K]> }>;

/** `custom` steps have a handler file; the others are library steps implemented by the SDK. */
export type StepKind = "custom" | "script" | "gitHubSource" | "imageBuild" | "helmDeployOverSsh" | "helmDeploy";

export interface Step<
  N extends string = string,
  I extends InputSpec = InputSpec,
  O extends AnyModel | null = AnyModel | null,
  Sec extends string = string,
  Cfg extends AnyConfigEntry = never,
> {
  readonly [StepBrand]: true;
  readonly kind: StepKind;
  readonly name: N;
  readonly description: string;
  readonly image: string;
  readonly input: I;
  readonly output: O;
  readonly secrets: readonly Secret<Sec>[];
  /** Config entries the step receives as `ctx.config`. */
  readonly config: readonly Cfg[];
  readonly timeoutSeconds?: number;
  readonly retries?: number;
  /** The step pod gets the instance's step ServiceAccount token (`Kubernetes:StepServiceAccount`). */
  readonly clusterAccess?: boolean;
  /** Library steps carry their own implementation and config. */
  readonly builtin?: LibraryImplementation;
}
/** Runtime part of a library step (not part of the public contract). */
export interface LibraryImplementation {
  /** Config as emitted in the manifest (JSON only; must not contain paths). */
  readonly manifestConfig: () => Record<string, unknown>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly run: (input: any, ctx: any) => Promise<unknown>;
}
export type LibraryStep = { readonly builtin: LibraryImplementation };
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type AnyStep = Step<string, any, AnyModel | null, any, any>;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type SecretsOf<S> = S extends Step<any, any, any, infer Sec, any> ? Sec : never;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type ConfigOf<S> = S extends Step<any, any, any, any, infer C> ? C : never;
export type OutputValue<S extends AnyStep> = S["output"] extends AnyModel ? Infer<S["output"]> : void;

/** Step names must be literals (identity, duplicate detection and mapper keys depend on it). */
export type LiteralName<N extends string> = N & (string extends N ? "❌ step name must be a string literal" : unknown);

/** Step names: lower-case kebab, ≤ 63 chars (usable as K8s label values and URL segments). */
export const STEP_NAME = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;
/** Names that cannot be steps: `input` is the pipeline input's root in mappings. */
export const RESERVED_STEP_NAMES: readonly string[] = ["input"];

export interface StepDef<N extends string, I extends InputSpec, O extends AnyModel | null, Sec extends readonly Secret[], Cfg extends readonly AnyConfigEntry[] = []> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /** Any image: the harness brings its own runtime. */
  image: string;
  input?: I;
  output?: O;
  secrets?: Sec;
  /** Config entries delivered as `ctx.config` (typed to exactly these). */
  config?: Cfg;
  timeoutSeconds?: number;
  /** Automatic retries (an idempotency claim). Default 0. */
  retries?: number;
  /**
   * Ask for a Kubernetes API token in the step pod: the instance's step ServiceAccount
   * (`Kubernetes:StepServiceAccount`), if it grants one. Default false (no token).
   */
  clusterAccess?: boolean;
}

/** `configFrom`: a library step's definition; the config entries it references are added to the step's `config`. */
export function makeStep(kind: StepKind, def: Record<string, unknown>, builtin?: LibraryImplementation, configFrom?: unknown): AnyStep {
  const config = new Map<string, AnyConfigEntry>();
  for (const e of (def.config as readonly AnyConfigEntry[] | undefined) ?? []) if (!config.has(e.name)) config.set(e.name, e);
  configRefsIn(configFrom, config);
  const s = {
    [STEP]: true,
    kind,
    name: def.name,
    description: def.description,
    image: def.image,
    input: Object.freeze({ ...((def.input as object | undefined) ?? {}) }),
    output: def.output ?? null,
    secrets: Object.freeze([...((def.secrets as readonly Secret[] | undefined) ?? [])]),
    config: Object.freeze([...config.values()]),
    ...(def.timeoutSeconds !== undefined ? { timeoutSeconds: def.timeoutSeconds } : {}),
    ...(def.retries !== undefined ? { retries: def.retries } : {}),
    ...(def.clusterAccess === true ? { clusterAccess: true } : {}),
    ...(builtin ? { builtin } : {}),
  };
  return Object.freeze(s) as unknown as AnyStep;
}

export function isStep(v: unknown): v is AnyStep {
  return typeof v === "object" && v !== null && (v as Record<symbol, unknown>)[STEP] === true;
}

/** Define a custom step: a contract (name, image, inputs, output, secrets). Its handler lives in `handlers/<name>.ts`. */
export function step<
  const N extends string,
  const I extends InputSpec = {},
  O extends AnyModel | null = null,
  const Sec extends readonly Secret[] = [],
  const Cfg extends readonly AnyConfigEntry[] = [],
>(def: StepDef<N, I, O, Sec, Cfg>): Step<N, I, O, Sec[number]["name"], Cfg[number]> {
  return makeStep("custom", def as never) as never;
}
