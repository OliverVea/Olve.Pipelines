// Prototype of the @olve/pipelines authoring SDK (spike — see ../REPORT.md).
//
// Types are the point; the runtime is just enough for `describe` to record a manifest.
import { z } from "zod";
export { z };

// ─── Phantom brands (type-only) ──────────────────────────────────────────────────────────────

declare const ModelBrand: unique symbol;
declare const RefBrand: unique symbol;
declare const StepBrand: unique symbol;
declare const SkippedBrand: unique symbol;
declare const NotYetBrand: unique symbol;

type Primitive = string | number | boolean | bigint | null | undefined;
type Simplify<T> = { [K in keyof T]: T[K] } & {};
type Awaitable<T> = T | Promise<T>;
type IsUnion<T, U = T> = T extends unknown ? ([U] extends [T] ? false : true) : never;
/** `Ok` when there is no error, else the error (a readable string-literal type). */
type OrError<Err, Ok> = [Err] extends [never] ? Ok : Err;

// ─── Models ──────────────────────────────────────────────────────────────────────────────────

export interface Model<N extends string = string, S extends z.ZodType = z.ZodType> {
  readonly kind: "model";
  readonly name: N;
  readonly schema: S;
}
export type AnyModel = Model<string, z.ZodType>;

/** Output type of a model, nominally branded with the model name (optional brand: plain literals still assign). */
export type Infer<M extends AnyModel> = z.output<M["schema"]> & { readonly [ModelBrand]?: M["name"] };
/** What a trigger mapping must produce: zod *input* type (defaults make fields optional). */
export type InferInput<M extends AnyModel> = z.input<M["schema"]>;
type Plain<M extends AnyModel> = z.output<M["schema"]>;

export function model<const N extends string, S extends z.ZodType>(name: N, schema: S): Model<N, S> {
  return { kind: "model", name, schema };
}

const FileRefSchema = z
  .object({ run: z.number(), step: z.string(), attempt: z.number(), path: z.string(), sha256: z.string() })
  .brand<"FileRef">()
  .meta({ "x-olve-kind": "fileRef" }); // so the service/GUI can tell file refs apart in the JSON Schema
/** A file/dir in some step's bundle. Branded: only `ctx.output.file/dir` or a Ref can produce one. */
export type FileRef = z.output<typeof FileRefSchema>;
export const fileRef = () => FileRefSchema;

export interface Optional<M extends AnyModel = AnyModel> {
  readonly kind: "optional";
  readonly model: M;
}
export function optional<M extends AnyModel>(model: M): Optional<M> {
  return { kind: "optional", model };
}

// ─── Secrets ─────────────────────────────────────────────────────────────────────────────────

export interface Secret<N extends string = string> {
  readonly kind: "secret";
  readonly name: N;
  readonly description: string;
}
export function secret<const N extends string>(name: N, description: string): Secret<N> {
  return { kind: "secret", name, description };
}
export type DeclaredSecrets<N extends string> = { readonly [K in N]: string };

// ─── Steps ───────────────────────────────────────────────────────────────────────────────────

/** A step input entry: a model (default-matchable), optional(model), or a plain zod schema (mapper required). */
export type InputEntry = AnyModel | Optional | z.ZodType;
export type InputSpec = { readonly [key: string]: InputEntry };

type InferEntry<E> =
  E extends Optional<infer M> ? Infer<M> | undefined
  : E extends AnyModel ? Infer<E>
  : E extends z.ZodType ? z.output<E>
  : never;
export type StepInput<I> = Simplify<{ -readonly [K in keyof I]: InferEntry<I[K]> }>;

export type StepKind = "custom" | "script" | "gitCheckout" | "imageBuild" | "wait";

export interface Step<
  N extends string = string,
  I extends InputSpec = InputSpec,
  O extends AnyModel | null = AnyModel | null,
  Sec extends string = string,
> {
  readonly [StepBrand]: true;
  readonly kind: StepKind;
  readonly name: N;
  readonly image: string | null;
  readonly input: I;
  readonly output: O;
  readonly secrets: readonly Secret<Sec>[];
  readonly timeoutSeconds?: number;
  readonly retries?: number;
  /** Library steps carry their own implementation. */
  readonly builtin?: { readonly config: unknown };
}
export type LibraryStep = { readonly builtin: { readonly config: unknown } };
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type AnyStep = Step<string, any, AnyModel | null, any>;

type SecretsOf<S> = S extends Step<any, any, any, infer Sec> ? Sec : never;
type OutputValue<S extends AnyStep> = S["output"] extends AnyModel ? Infer<S["output"]> : void;

/** Step names must be literals (identity, duplicate detection and `in` keys depend on it). */
export type LiteralName<N extends string> = N & (string extends N ? "❌ step name must be a string literal" : unknown);

export interface StepDef<N extends string, I extends InputSpec, O extends AnyModel | null, Sec extends readonly Secret[]> {
  name: LiteralName<N>;
  image: string;
  input?: I;
  output?: O;
  secrets?: Sec;
  timeoutSeconds?: number;
  retries?: number;
}

function makeStep(kind: StepKind, def: Record<string, unknown>, builtin?: { config: unknown }): AnyStep {
  return {
    kind,
    name: def.name,
    image: (def.image as string | undefined) ?? null,
    input: def.input ?? {},
    output: def.output ?? null,
    secrets: def.secrets ?? [],
    timeoutSeconds: def.timeoutSeconds,
    retries: def.retries,
    builtin,
  } as unknown as AnyStep;
}

export function step<
  const N extends string,
  const I extends InputSpec = {},
  O extends AnyModel | null = null,
  const Sec extends readonly Secret[] = [],
>(def: StepDef<N, I, O, Sec>): Step<N, I, O, Sec[number]["name"]> {
  return makeStep("custom", def as never) as never;
}

// ─── Handlers ────────────────────────────────────────────────────────────────────────────────

export interface Skipped {
  readonly [SkippedBrand]: true;
  readonly reason: string;
}
export interface RunInfo {
  readonly id: string;
  readonly number: number;
  /** Derived version stamp, e.g. 2026.10.21.412 */
  readonly version: string;
}
export interface ShOptions {
  env?: Record<string, string>;
  cwd?: string;
}
export interface Sh {
  (script: string, opts?: ShOptions): Promise<void>;
  text(script: string, opts?: ShOptions): Promise<string>;
}
export interface Http {
  getJson<T = unknown>(url: string, init?: { headers?: Record<string, string> }): Promise<T>;
  expectOk(url: string): Promise<void>;
}
export interface Ctx<Sec extends string> {
  readonly secrets: DeclaredSecrets<Sec>;
  readonly sh: Sh;
  readText(path: string): Promise<string>;
  readJson<T = unknown>(path: string): Promise<T>;
  readonly files: { fetch(ref: FileRef): Promise<string> };
  readonly output: { file(path: string): Promise<FileRef>; dir(path: string): Promise<FileRef> };
  readonly http: Http;
  retry<T>(opts: { attempts: number; delaySeconds: number }, fn: () => Promise<T>): Promise<T>;
  log(message: string, fields?: Record<string, unknown>): void;
  readonly signal: AbortSignal;
  readonly run: RunInfo;
  checkout(repo: string, sha: string): Promise<string>;
  skip(reason: string): Skipped;
}

export type HandlerResult<S extends AnyStep> = OutputValue<S> | Skipped;
export type Handler<S extends AnyStep> = (
  input: StepInput<S["input"]>,
  ctx: Ctx<SecretsOf<S>>,
) => Awaitable<HandlerResult<S>>;

export interface Implementation<S extends AnyStep> {
  readonly step: S;
  readonly handler: Handler<S>;
}

/** Implement a custom step. Input/ctx/return types come from the step definition alone. */
export function implement<S extends AnyStep, R extends HandlerResult<S>>(
  step: S & (S extends LibraryStep ? "❌ library steps already carry their implementation" : unknown),
  handler: (input: StepInput<S["input"]>, ctx: Ctx<SecretsOf<S>>) => Awaitable<R & NoExtraFields<R, OutputValue<S>>>,
): Implementation<S> {
  return { step, handler: handler as Handler<S> };
}
/** Block-bodied/async returns get no excess-property check; this restores it for handler outputs. */
type NoExtraFields<R, T> = R extends Skipped | void
  ? unknown
  : { [K in Exclude<keyof R, keyof T>]: `❌ '${K & string}' is not a field of the output model` };

// ─── Library steps ───────────────────────────────────────────────────────────────────────────

export const Source = model("Source", z.object({ repo: z.string(), sha: z.string(), tree: fileRef() }));
export const BuiltImage = model("BuiltImage", z.object({ ref: z.string(), digest: z.string(), tar: fileRef() }));
export const FailureContext = model(
  "FailureContext",
  z.object({
    pipeline: z.string(),
    run: z.number(),
    step: z.string(),
    attempt: z.number(),
    reason: z.string(),
    logUrl: z.string().url(),
  }),
);

type RunValue<I, T = string> = T | ((input: StepInput<I>, run: RunInfo) => T);
export type Duration = `${number}${"s" | "m" | "h"}`;

export interface NotYet {
  readonly [NotYetBrand]: true;
}
export interface CheckCtx {
  readonly http: Http;
  log(message: string): void;
  notYet(): NotYet;
}
type AtLeastOne<T> = { [K in keyof T]-?: Required<Pick<T, K>> & Partial<Omit<T, K>> }[keyof T];

export const steps = {
  /** Runs a shell script. Inputs → env vars + input.json; reads/validates output.json if an output model is set. */
  script<
    const N extends string,
    const I extends InputSpec = {},
    O extends AnyModel | null = null,
    const Sec extends readonly Secret[] = [],
  >(def: StepDef<N, I, O, Sec> & { script: string; env?: Record<string, string> }): Step<N, I, O, Sec[number]["name"]> & LibraryStep {
    return makeStep("script", def as never, { config: { script: def.script, env: def.env } }) as never;
  },

  /** input { repo, sha } (plain fields — needs a mapper) → output Source */
  gitCheckout<const N extends string, TokenName extends string = never>(def: {
    name: LiteralName<N>;
    depth?: number;
    submodules?: boolean;
    token?: Secret<TokenName>;
  }): Step<N, { readonly repo: z.ZodString; readonly sha: z.ZodString }, typeof Source, TokenName> & LibraryStep {
    return makeStep(
      "gitCheckout",
      { ...def, image: "olve/git:latest", input: { repo: z.string(), sha: z.string() }, output: Source, secrets: def.token ? [def.token] : [] },
      { config: { depth: def.depth, submodules: def.submodules } },
    ) as never;
  },

  /** input { source: Source } → output BuiltImage. Run-dependent config may be a function of the inputs. */
  imageBuild<const N extends string, const Sec extends readonly Secret[] = []>(def: {
    name: LiteralName<N>;
    dockerfile?: string;
    context?: string;
    tag: RunValue<{ readonly source: typeof Source }>;
    buildArgs?: Record<string, RunValue<{ readonly source: typeof Source }>>;
    secrets?: Sec;
    timeoutSeconds?: number;
  }): Step<N, { readonly source: typeof Source }, typeof BuiltImage, Sec[number]["name"]> & LibraryStep {
    return makeStep(
      "imageBuild",
      { ...def, image: "gcr.io/kaniko-project/executor:debug", input: { source: Source }, output: BuiltImage },
      { config: { dockerfile: def.dockerfile, context: def.context, tag: def.tag, buildArgs: def.buildArgs } },
    ) as never;
  },

  /** No pod while waiting; resolved with a value validated against the output model. */
  wait<const N extends string, O extends AnyModel, const I extends InputSpec = {}>(def: {
    name: LiteralName<N>;
    input?: I;
    output: O;
    resolveBy: AtLeastOne<{
      manual: true;
      callback: true;
      check: { every: Duration; run(input: StepInput<I>, ctx: CheckCtx): Promise<Plain<O> | NotYet> };
    }>;
    timeoutHours?: number;
  }): Step<N, I, O, never> & LibraryStep {
    return makeStep("wait", def as never, { config: { resolveBy: def.resolveBy, timeoutHours: def.timeoutHours } }) as never;
  },
};

// ─── Refs (what mappers see) ─────────────────────────────────────────────────────────────────

/**
 * A typed reference into the run pool. `symbol &` makes template strings, `+` and `===` against
 * values compile errors (TS2731 / TS2469 / TS2367); only property access and literals remain.
 * Fields of a maybe-undefined ref are themselves maybe-undefined (optional-chaining semantics).
 */
export type Ref<T> = symbol & { readonly [RefBrand]: T } & RefFields<T>;
type RefFields<T> = [NonNullable<T>] extends [Primitive]
  ? {}
  : NonNullable<T> extends readonly unknown[]
    ? {}
    : { readonly [K in keyof NonNullable<T> & string]-?: Ref<NonNullable<T>[K] | (undefined extends T ? undefined : never)> };

/** What a mapper may put where a T is expected: a Ref<T>, a literal, or an object of those. */
export type Mappable<T> =
  | Ref<T>
  | (T extends Primitive
      ? T
      : T extends readonly unknown[]
        ? never
        : { [K in keyof T]: Mappable<T[K]> } & { readonly [RefBrand]?: never }); // a Ref of another model can't sneak in structurally

export type MapperResult<S extends AnyStep> = { [K in keyof S["input"]]: Mappable<InferEntry<S["input"][K]>> };

// ─── Availability (builder state) ────────────────────────────────────────────────────────────

interface Slot<M extends string | null = string | null, V = unknown, Maybe extends boolean = boolean> {
  model: M;
  value: V;
  maybe: Maybe;
}
type AvailMap = { [step: string]: Slot };
export type InitialAvail<In extends AnyModel> = { input: Slot<In["name"], Infer<In>, false> };

/** The mapper argument: keyed by step name + `input`; maybe-available outputs are `Ref<T | undefined>`. */
export type InRefs<A extends AvailMap> = {
  readonly [K in keyof A as A[K]["model"] extends null ? never : K]: Ref<
    A[K]["maybe"] extends true ? A[K]["value"] | undefined : A[K]["value"]
  >;
};

type ProducersOf<A extends AvailMap, MN> = { [K in keyof A]: A[K]["model"] extends MN ? K : never }[keyof A];
type DefiniteProducersOf<A extends AvailMap, MN> = {
  [K in keyof A]: A[K]["model"] extends MN ? (A[K]["maybe"] extends false ? K : never) : never;
}[keyof A];

type EntryError<SN extends string, Key extends string, E, A extends AvailMap> =
  E extends Optional<infer M>
    ? [ProducersOf<A, M["name"]>] extends [never]
      ? `❌ ${M["name"]} (input '${Key}' of step '${SN}') is not available at this stage`
      : IsUnion<ProducersOf<A, M["name"]>> extends true
        ? `❌ ${M["name"]} (input '${Key}' of step '${SN}') has several producers; use a mapper to pick one`
        : never
    : E extends AnyModel
      ? [ProducersOf<A, E["name"]>] extends [never]
        ? `❌ ${E["name"]} (input '${Key}' of step '${SN}') is not available at this stage`
        : [DefiniteProducersOf<A, E["name"]>] extends [never]
          ? `❌ ${E["name"]} (input '${Key}' of step '${SN}') is only maybe-available (produced inside a when block); declare it optional(${E["name"]})`
          : IsUnion<ProducersOf<A, E["name"]>> extends true
            ? `❌ ${E["name"]} (input '${Key}' of step '${SN}') has several producers; use a mapper to pick one`
            : never
      : `❌ input '${Key}' of step '${SN}' is a plain schema, not a model, so it cannot be matched by model; add a mapper`;

type DefaultMatchError<S extends AnyStep, A extends AvailMap> = {
  [K in keyof S["input"] & string]: EntryError<S["name"], K, S["input"][K], A>;
}[keyof S["input"] & string] extends infer X extends string ? X : never; // `infer` forces display of the literal

type NameError<S extends AnyStep, A extends AvailMap, SiblingNames> =
  // NB: no `string extends S["name"]` test here — it poisons inference of mixed stages (see REPORT.md);
  // non-literal names are rejected in step() instead.
  S["name"] extends "input" ? "❌ 'input' is reserved for the pipeline input and cannot be a step name"
  : S["name"] extends keyof A ? `❌ duplicate step name '${S["name"]}' (already used in an earlier stage)`
  : S["name"] extends SiblingNames ? `❌ duplicate step name '${S["name"]}' (used twice in this stage)`
  : never;

// ─── Stage entries ───────────────────────────────────────────────────────────────────────────

export type MapperFn<S extends AnyStep, A extends AvailMap> = (refs: InRefs<A>) => MapperResult<S>;
export type StageEntry<A extends AvailMap> = AnyStep | readonly [AnyStep, (refs: InRefs<A>) => unknown];


type StepOfEntry<X> = X extends readonly [infer S extends AnyStep, unknown] ? S : X extends AnyStep ? X : never;
type SiblingNames<E extends readonly unknown[], K> = {
  [J in keyof E]: J extends K ? never : StepOfEntry<E[J]>["name"];
}[number];

/**
 * Per-entry check. Inference constraints learned the hard way (see REPORT.md):
 *  - the [step, mapper] branch must be exactly `readonly [S, (refs) => …]` — wrapping S or the whole
 *    mapper type in a conditional/intersection breaks inference of E or contextual typing of `refs`;
 *    so name errors of tuple entries are reported on the mapper's return type instead;
 *  - nothing may look at *neighbouring* entries through E except via NoInfer (sibling names only).
 */
export type CheckEntry<E extends readonly unknown[], K, X, A extends AvailMap> =
  X extends readonly [infer S extends AnyStep, unknown]
    ? readonly [S, (refs: InRefs<A>) => OrError<NameError<S, A, SiblingNames<E, K>>, MapperResult<S>>]
    : X extends AnyStep
      ? string extends X["name"] ? X : OrError<NameError<X, A, SiblingNames<E, K>> | DefaultMatchError<X, A>, X>
      : "❌ a stage entry must be a step or a [step, mapper] pair";

// NB: the template must mention `E[K]` literally, or reverse-mapped inference of E fails.
export type CheckStage<E extends readonly unknown[], A extends AvailMap> = { [K in keyof E]: CheckEntry<NoInfer<E>, K, E[K], A> };

type OutputsOf<E extends readonly unknown[], Maybe extends boolean> = {
  [S in StepOfEntry<E[number]> as S["name"]]: Slot<
    S["output"] extends AnyModel ? S["output"]["name"] : null,
    OutputValue<S>,
    Maybe
  >;
};
type MakeMaybe<X extends AvailMap> = { [K in keyof X]: Slot<X[K]["model"], X[K]["value"], true> };

// ─── Conditions / triggers ───────────────────────────────────────────────────────────────────

type IsLiteralLike<T> = [T] extends [string | number | boolean]
  ? string extends T ? false : number extends T ? false : true
  : false;
export type WhenCond<In extends AnyModel> = {
  [K in keyof Plain<In> & string]?: Exclude<Plain<In>[K], undefined> extends infer L
    ? IsLiteralLike<L> extends true
      ? L | readonly L[]
      : `❌ '${K}' is not a literal-like field (enum, boolean or literal); when cannot match on it`
    : never;
};

/** Minimal GitHub push payload (real payloads have repository as an object, not a string). */
export interface PushEvent {
  readonly ref: string;
  readonly before: string;
  readonly after: string;
  readonly repository: { readonly full_name: string; readonly clone_url: string };
}

type CheckHeader<V, Allowed extends string> = V extends `$SECRET:${infer X}`
  ? X extends Allowed ? V : `❌ secret '${X}' is not declared in this poll trigger's secrets`
  : V;

/** Rejects extra fields in a trigger mapping (arrow-function returns get no excess-property check). */
type Exact<R, T> = R & { [K in Exclude<keyof R, keyof T>]: `❌ '${K & string}' is not a field of the pipeline input` };

type StepNames<A extends AvailMap> = Exclude<keyof A, "input"> & string;

// ─── Pipeline builder ────────────────────────────────────────────────────────────────────────

type Mapping = { ref: string } | { const: unknown } | { [key: string]: Mapping };
interface StageStepRecord {
  step: AnyStep;
  input: Record<string, Mapping>;
  mapped: boolean;
}
type StageRecord = { steps: StageStepRecord[] } | { when: Record<string, unknown[]>; stages: StageRecord[] };

const REF_PATH = Symbol("olve.refPath");

function makeRef(path: string[]): unknown {
  const target = () => {};
  return new Proxy(target, {
    get(_t, prop) {
      if (prop === REF_PATH) return path.join(".");
      if (prop === Symbol.toPrimitive || prop === "toString" || prop === "valueOf" || prop === "toJSON") {
        return () => {
          throw new Error(`computed value from ref '${path.join(".")}' in a mapper; only property access and literals are allowed`);
        };
      }
      if (typeof prop === "symbol") return undefined;
      return makeRef([...path, prop]);
    },
  });
}

function recordMapping(value: unknown): Mapping {
  if ((typeof value === "function" || typeof value === "object") && value !== null) {
    const path = (value as Record<symbol, unknown>)[REF_PATH];
    if (typeof path === "string") return { ref: path };
    if (typeof value === "function") throw new Error("functions are not allowed in mapper results");
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, recordMapping(v)]));
  }
  return { const: value };
}

export class Pipeline<In extends AnyModel, A extends AvailMap> {
  /** Phantom: the availability map accumulated so far. */
  declare readonly __avail: A;
  readonly #name: string;
  readonly #description: string | undefined;
  readonly #input: In;
  readonly #stages: StageRecord[] = [];
  readonly #triggers: unknown[] = [];
  readonly #failureHandlers: unknown[] = [];
  readonly #produced = new Map<string, { model: string | null; maybe: boolean }>();

  constructor(def: { name: string; description?: string; input: In }, inherit?: Pipeline<In, AvailMap>) {
    this.#name = def.name;
    this.#description = def.description;
    this.#input = def.input;
    if (inherit) for (const [k, v] of inherit.#produced) this.#produced.set(k, v);
    else this.#produced.set("input", { model: def.input.name, maybe: false });
  }

  onPush<R extends InferInput<In>>(map: (event: PushEvent) => Exact<R, InferInput<In>>): this {
    this.#triggers.push({ kind: "push", map });
    return this;
  }

  onPoll<R extends InferInput<In>, const Sec extends readonly Secret[] = [], const H extends Record<string, string> = {}>(
    cfg: {
      url: string;
      secrets?: Sec;
      headers?: { [K in keyof H]: CheckHeader<H[K], Sec[number]["name"]> };
      valuePath: string;
      intervalSeconds: number;
    },
    map: (value: string) => Exact<R, InferInput<In>>,
  ): this {
    this.#triggers.push({ kind: "poll", url: cfg.url, valuePath: cfg.valuePath, intervalSeconds: cfg.intervalSeconds, map });
    return this;
  }

  /**
   * Add a stage (steps run in parallel). Entries: `step`, `step, mapper`, `[step, mapper]`.
   * Every entry is checked against what was available *before* this stage.
   */
  stage<const E extends readonly StageEntry<A>[]>(
    ...entries: CheckStage<E, A>
  ): Pipeline<In, Simplify<A & OutputsOf<E, false>>>;
  stage(...entries: readonly unknown[]): unknown {
    const records: StageStepRecord[] = [];
    const list = entries;
    const refs = new Proxy({}, { get: (_t, p) => (typeof p === "string" ? makeRef([p]) : undefined) });
    for (let i = 0; i < list.length; i++) {
      const e = list[i];
      let s: AnyStep;
      let mapper: ((r: unknown) => unknown) | undefined;
      if (Array.isArray(e)) [s, mapper] = e as [AnyStep, (r: unknown) => unknown];
      else if (typeof e === "function") continue; // consumed with the previous step
      else {
        s = e as AnyStep;
        const nxt = list[i + 1];
        if (typeof nxt === "function") mapper = nxt as (r: unknown) => unknown;
      }
      records.push({ step: s, mapped: !!mapper, input: mapper ? (recordMapping(mapper(refs)) as Record<string, Mapping>) : this.#defaultMatch(s) });
    }
    for (const r of records) {
      if (this.#produced.has(r.step.name)) throw new Error(`duplicate step name '${r.step.name}'`);
      this.#produced.set(r.step.name, { model: r.step.output?.name ?? null, maybe: false });
    }
    this.#stages.push({ steps: records });
    return this as never;
  }

  #defaultMatch(s: AnyStep): Record<string, Mapping> {
    const out: Record<string, Mapping> = {};
    for (const [key, entry] of Object.entries(s.input as Record<string, InputEntry>)) {
      const m = (entry as { kind?: string }).kind === "optional" ? (entry as Optional).model : (entry as AnyModel);
      if ((m as { kind?: string }).kind !== "model") throw new Error(`step '${s.name}': input '${key}' needs a mapper`);
      const producers = [...this.#produced].filter(([, v]) => v.model === m.name).map(([k]) => k);
      if (producers.length !== 1) throw new Error(`step '${s.name}': ${m.name} has ${producers.length} producers`);
      out[key] = { ref: producers[0]! };
    }
    return out;
  }

  /** Declarative condition on literal-like pipeline-input fields. Outputs produced inside become maybe-available after. */
  when<B extends AvailMap>(
    cond: WhenCond<In>,
    block: (p: Pipeline<In, A>) => Pipeline<In, B>,
  ): Pipeline<In, Simplify<A & MakeMaybe<Omit<B, keyof A>>>> {
    const inner = new Pipeline<In, A>({ name: this.#name, input: this.#input }, this as never);
    block(inner);
    for (const [k, v] of inner.#produced) if (!this.#produced.has(k)) this.#produced.set(k, { ...v, maybe: true });
    const normalized = Object.fromEntries(Object.entries(cond).map(([k, v]) => [k, Array.isArray(v) ? v : [v]]));
    this.#stages.push({ when: normalized, stages: inner.#stages });
    return this as never;
  }

  onFailure<S extends AnyStep>(
    handler: S &
      ([Exclude<S["input"][keyof S["input"]], typeof FailureContext>] extends [never]
        ? unknown
        : "❌ a failure handler's inputs must all be FailureContext"),
    opts?: { steps?: readonly Step<StepNames<A>, any, any, any>[] },
  ): this {
    const h = handler as AnyStep;
    this.#failureHandlers.push({
      step: { name: h.name, kind: h.kind, image: h.image, secrets: h.secrets.map((s) => s.name) },
      steps: opts?.steps?.map((s) => s.name) ?? "all",
    });
    return this;
  }

  /** describe mode: plain-data manifest (subset of spec §4). */
  describe(): unknown {
    const models = new Map<string, AnyModel>([[this.#input.name, this.#input]]);
    const secrets = new Map<string, Secret>();
    const stage = (r: StageRecord): unknown =>
      "steps" in r
        ? {
            steps: r.steps.map(({ step: s, input }) => {
              if (s.output) models.set(s.output.name, s.output);
              for (const e of Object.values(s.input as Record<string, InputEntry>)) {
                const m = (e as Optional).kind === "optional" ? (e as Optional).model : e;
                if ((m as AnyModel).kind === "model") models.set((m as AnyModel).name, m as AnyModel);
              }
              for (const sec of s.secrets) secrets.set(sec.name, sec);
              return {
                name: s.name,
                kind: s.kind,
                image: s.image,
                input,
                output: s.output?.name ?? null,
                secrets: s.secrets.map((x) => x.name),
              };
            }),
          }
        : { when: r.when, stages: r.stages.map(stage) };
    const stages = this.#stages.map(stage);
    return {
      apiVersion: "2.0",
      name: this.#name,
      description: this.#description,
      input: this.#input.name,
      models: Object.fromEntries([...models].map(([k, m]) => [k, z.toJSONSchema(m.schema, { unrepresentable: "any" })])),
      secrets: [...secrets.values()].map(({ name, description }) => ({ name, description })),
      triggers: this.#triggers.map((t) => {
        const { map: _map, ...rest } = t as { map: unknown };
        return rest;
      }),
      stages,
      failureHandlers: this.#failureHandlers,
    };
  }
}

export function pipeline<In extends AnyModel>(def: {
  name: string;
  description?: string;
  input: In;
}): Pipeline<In, InitialAvail<In>> {
  return new Pipeline(def) as never;
}
