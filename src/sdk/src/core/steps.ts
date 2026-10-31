import type { z } from 'zod';
import { STEP, type StepBrand } from './brands.ts';
import type { AnyModel, Infer, Optional } from './models.ts';
import type { External } from './external.ts';
import type { AnyConfigEntry } from './config.ts';
import { configRefsIn } from './config-refs.ts';
import type { Secret } from './secrets.ts';
import type { Simplify } from './types.ts';

/**
 * A step input entry: a model (matchable by model), `optional(model)`, a plain zod schema (mapper required),
 * or `external(model, …)`, answered from outside the run.
 */
export type InputEntry = AnyModel | Optional | External | z.ZodType;
export type InputSpec = { readonly [key: string]: InputEntry };

export type InferEntry<E> =
  E extends Optional<infer M>
    ? Infer<M> | undefined
    : E extends External<infer M>
      ? Infer<M>
      : E extends AnyModel
        ? Infer<E>
        : E extends z.ZodType
          ? z.output<E>
          : never;
/** What a handler receives: the output view of every input entry. */
export type StepInput<I> = Simplify<{ -readonly [K in keyof I]: InferEntry<I[K]> }>;

/**
 * A label for what a step is, shown in the UI. `custom` is the default; a reusable step names its
 * kind, e.g. `gitHubSource`. Packages prefix theirs (`acme/deploy`).
 */
export type StepKind = string;

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
  /** How the step is set up, as written in the definition (may hold config references). */
  readonly settings?: Readonly<Record<string, unknown>>;
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type AnyStep = Step<string, any, AnyModel | null, any, any>;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type SecretsOf<S> = S extends Step<any, any, any, infer Sec, any> ? Sec : never;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type ConfigOf<S> = S extends Step<any, any, any, any, infer C> ? C : never;
export type OutputValue<S extends AnyStep> = S['output'] extends AnyModel
  ? Infer<S['output']>
  : void;

/** Step names must be literals (identity, duplicate detection and mapper keys depend on it). */
export type LiteralName<N extends string> = N &
  (string extends N ? '❌ step name must be a string literal' : unknown);

export { STEP_NAME } from './models.ts';
/** Names that cannot be steps: `input` is the pipeline input's root in mappings. */
export const RESERVED_STEP_NAMES: readonly string[] = ['input'];

export interface StepDef<
  N extends string,
  I extends InputSpec,
  O extends AnyModel | null,
  Sec extends readonly Secret[],
  Cfg extends readonly AnyConfigEntry[] = [],
> {
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
  /** A label for the step's kind (default `custom`). */
  kind?: StepKind;
  /**
   * How the step is set up, shown in the manifest and part of the step's identity: the parameters of
   * a reusable step. JSON values and config references (`target.prod.host`); every config entry it
   * references is delivered to the step, and `resolveConfigRefs(value, ctx.config)` resolves them.
   */
  settings?: Readonly<Record<string, unknown>>;
}

function makeStep(def: Record<string, unknown>): AnyStep {
  const config = new Map<string, AnyConfigEntry>();
  for (const e of (def.config as readonly AnyConfigEntry[] | undefined) ?? [])
    if (!config.has(e.name)) config.set(e.name, e);
  configRefsIn(def.settings, config);
  const s = {
    [STEP]: true,
    kind: def.kind ?? 'custom',
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
    ...(def.settings !== undefined ? { settings: def.settings } : {}),
  };
  return Object.freeze(s) as unknown as AnyStep;
}

export function isStep(v: unknown): v is AnyStep {
  return (
    typeof v === 'object' && v !== null && (v as Record<symbol, unknown>)[STEP] === true
  );
}

/**
 * Define a step: a contract (name, image, inputs, output, secrets). `implement(step, fn)` gives it
 * its handler; that is what a pipeline stages.
 */
export function step<
  const N extends string,
  const I extends InputSpec = {},
  O extends AnyModel | null = null,
  const Sec extends readonly Secret[] = [],
  const Cfg extends readonly AnyConfigEntry[] = [],
>(def: StepDef<N, I, O, Sec, Cfg>): Step<N, I, O, Sec[number]['name'], Cfg[number]> {
  return makeStep(def as never) as never;
}
