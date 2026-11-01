import { PIPELINE } from '../core/brands.ts';
import type { AnyModel, Infer, InferInput, Plain } from '../core/models.ts';
import type { Secret } from '../core/secrets.ts';
import type { AnyStep, OutputValue, Step } from '../core/steps.ts';
import { isStep } from '../core/steps.ts';
import type { WebhookEvent, WebhookVerify } from '../core/webhooks.ts';
import type { IsUnion, OrError, Simplify } from '../core/types.ts';
import type { Optional } from '../core/models.ts';
import type { External } from '../core/external.ts';
import type { FailureContext } from '../library/models.ts';
import type { MapperResult, Ref } from './refs.ts';
import {
  isConfigRef,
  narrowConfigRef,
  unionAt,
  type ConfigNode,
  type ConfigRef,
  type DiscriminatorOf,
  type VariantOf,
} from '../core/config.ts';

// ─── Availability (builder state, type level) ────────────────────────────────────────────────

interface Slot<
  M extends string | null = string | null,
  V = unknown,
  Maybe extends boolean = boolean,
> {
  model: M;
  value: V;
  maybe: Maybe;
}
type AvailMap = { [step: string]: Slot };
export type InitialAvail<In extends AnyModel> = {
  input: Slot<In['name'], Infer<In>, false>;
};

/** The mapper argument: keyed by step name + `input`; maybe-available outputs are `Ref<T | undefined>`. */
export type InRefs<A extends AvailMap> = {
  readonly [K in keyof A as A[K]['model'] extends null ? never : K]: Ref<
    A[K]['maybe'] extends true ? A[K]['value'] | undefined : A[K]['value']
  >;
};

type ProducersOf<A extends AvailMap, MN> = {
  [K in keyof A]: A[K]['model'] extends MN ? K : never;
}[keyof A];
type DefiniteProducersOf<A extends AvailMap, MN> = {
  [K in keyof A]: A[K]['model'] extends MN
    ? A[K]['maybe'] extends false
      ? K
      : never
    : never;
}[keyof A];

type EntryError<
  SN extends string,
  Key extends string,
  E,
  A extends AvailMap,
> = E extends External
  ? never
  : E extends Optional<infer M>
    ? [ProducersOf<A, M['name']>] extends [never]
      ? `❌ ${M['name']} (input '${Key}' of step '${SN}') is not available at this stage`
      : IsUnion<ProducersOf<A, M['name']>> extends true
        ? `❌ ${M['name']} (input '${Key}' of step '${SN}') has several producers; use a mapper to pick one`
        : never
    : E extends AnyModel
      ? [ProducersOf<A, E['name']>] extends [never]
        ? `❌ ${E['name']} (input '${Key}' of step '${SN}') is not available at this stage`
        : [DefiniteProducersOf<A, E['name']>] extends [never]
          ? `❌ ${E['name']} (input '${Key}' of step '${SN}') is only maybe-available (produced inside a when block); declare it optional(${E['name']})`
          : IsUnion<ProducersOf<A, E['name']>> extends true
            ? `❌ ${E['name']} (input '${Key}' of step '${SN}') has several producers; use a mapper to pick one`
            : never
      : `❌ input '${Key}' of step '${SN}' is a plain schema, not a model, so it cannot be matched by model; add a mapper`;

type DefaultMatchError<S extends AnyStep, A extends AvailMap> = {
  [K in keyof S['input'] & string]: EntryError<S['name'], K, S['input'][K], A>;
}[keyof S['input'] & string] extends infer X extends string
  ? X
  : never; // `infer` forces display of the literal

type NameError<S extends AnyStep, A extends AvailMap, SiblingNames> =
  // NB: no `string extends S["name"]` test here — it poisons inference of mixed stages;
  // non-literal names are rejected in step() instead.
  S['name'] extends 'input'
    ? "❌ 'input' is reserved for the pipeline input and cannot be a step name"
    : S['name'] extends keyof A
      ? `❌ duplicate step name '${S['name']}' (already used in an earlier stage)`
      : S['name'] extends SiblingNames
        ? `❌ duplicate step name '${S['name']}' (used twice in this stage)`
        : never;

// ─── Stage entries ───────────────────────────────────────────────────────────────────────────

export type MapperFn<S extends AnyStep, A extends AvailMap> = (
  from: InRefs<A>,
) => MapperResult<S>;
export type StageEntry<A extends AvailMap> =
  AnyStep | readonly [AnyStep, (from: InRefs<A>) => unknown];

type StepOfEntry<X> = X extends readonly [infer S extends AnyStep, unknown]
  ? S
  : X extends AnyStep
    ? X
    : never;
type SiblingNames<E extends readonly unknown[], K> = {
  [J in keyof E]: J extends K ? never : StepOfEntry<E[J]>['name'];
}[number];

/**
 * Per-entry check. Inference constraints:
 *  - the [step, mapper] branch must be exactly `readonly [S, (from) => …]` — wrapping S or the whole
 *    mapper type in a conditional/intersection breaks inference of E or contextual typing of `from`;
 *    so name errors of tuple entries are reported on the mapper's return type instead;
 *  - nothing may look at *neighbouring* entries through E except via NoInfer (sibling names only).
 */
export type CheckEntry<
  E extends readonly unknown[],
  K,
  X,
  A extends AvailMap,
> = X extends readonly [infer S extends AnyStep, unknown]
  ? readonly [
      S,
      (from: InRefs<A>) => OrError<NameError<S, A, SiblingNames<E, K>>, MapperResult<S>>,
    ]
  : X extends AnyStep
    ? string extends X['name']
      ? X
      : OrError<NameError<X, A, SiblingNames<E, K>> | DefaultMatchError<X, A>, X>
    : '❌ a stage entry must be a step or a [step, mapper] pair';

// NB: the template must mention `E[K]` literally, or reverse-mapped inference of E fails.
export type CheckStage<E extends readonly unknown[], A extends AvailMap> = {
  [K in keyof E]: CheckEntry<NoInfer<E>, K, E[K], A>;
};

type OutputsOf<E extends readonly unknown[], Maybe extends boolean> = {
  [S in StepOfEntry<E[number]> as S['name']]: Slot<
    S['output'] extends AnyModel ? S['output']['name'] : null,
    OutputValue<S>,
    Maybe
  >;
};
type MakeMaybe<X extends AvailMap> = {
  [K in keyof X]: Slot<X[K]['model'], X[K]['value'], true>;
};

// ─── select (branching on config) ────────────────────────────────────────────────────────────

type KeysOfUnion<T> = T extends unknown ? keyof T : never;
type SlotIn<B, K extends PropertyKey> = B extends { [P in K]: infer X extends Slot }
  ? X
  : never;
/** A case function: the pipeline so far, and the select's reference narrowed to the case's variant. */
export type SelectCase<In extends AnyModel, A extends AvailMap, R> = (
  p: Pipeline<In, A>,
  ref: R,
) => Pipeline<In, AvailMap>;
export type SelectCases<In extends AnyModel, A extends AvailMap, U, D extends string> = {
  readonly [V in Extract<U[D & keyof U], string>]: SelectCase<
    In,
    A,
    ConfigNode<VariantOf<U, D, V>>
  >;
};
type CaseAvail<C> = {
  [K in keyof C]: C[K] extends (...args: never[]) => Pipeline<AnyModel, infer B>
    ? B
    : never;
}[keyof C];
/**
 * After a select: a step every case has is as available as in its cases (one case always runs);
 * a step only some cases have is maybe-available.
 */
export type AfterSelect<A extends AvailMap, B> = Simplify<
  A & {
    [K in Exclude<keyof B, keyof A>]: Slot<
      SlotIn<B, K>['model'],
      SlotIn<B, K>['value'],
      SlotIn<B, K>['maybe']
    >;
  } & {
    [K in Exclude<KeysOfUnion<B>, keyof B | keyof A>]: Slot<
      SlotIn<B, K>['model'],
      SlotIn<B, K>['value'],
      true
    >;
  }
>;
type SelectOnError<U> = [DiscriminatorOf<U>] extends [never]
  ? '❌ select needs a reference to a discriminated union in config (z.discriminatedUnion)'
  : IsUnion<DiscriminatorOf<U>> extends true
    ? "❌ the union has more than one literal field; select can't tell which is the discriminator"
    : never;

// ─── Conditions / triggers ───────────────────────────────────────────────────────────────────

type IsLiteralLike<T> = [T] extends [string | number | boolean]
  ? string extends T
    ? false
    : number extends T
      ? false
      : true
  : false;
export type WhenCond<In extends AnyModel> = {
  [K in keyof Plain<In> & string]?: Exclude<Plain<In>[K], undefined> extends infer L
    ? IsLiteralLike<L> extends true
      ? L | readonly L[]
      : `❌ '${K}' is not a literal-like field (enum, boolean or literal); when cannot match on it`
    : never;
};

/** The push event the service sends a push trigger mapper: exactly these fields, nothing more. */
export interface PushEvent {
  readonly ref: string;
  readonly after: string;
  readonly repository: { readonly full_name: string };
}

type CheckHeader<V, Allowed extends string> = V extends `$SECRET:${infer X}`
  ? X extends Allowed
    ? V
    : `❌ secret '${X}' is not declared in this poll trigger's secrets`
  : V;

/** Rejects extra fields in a trigger mapping (arrow-function returns get no excess-property check). */
type Exact<R, T> = R & {
  [
    K in Exclude<keyof R, keyof T>
  ]: `❌ '${K & string}' is not a field of the pipeline input`;
};

type StepNames<A extends AvailMap> = Exclude<keyof A, 'input'> & string;

// ─── Runtime records (read by describe/harness) ──────────────────────────────────────────────

export type MapperFunction = (from: unknown) => unknown;
export interface StageStepNode {
  readonly step: AnyStep;
  readonly mapper?: MapperFunction;
}
export type StageNode =
  | { readonly kind: 'stage'; readonly steps: readonly StageStepNode[] }
  | {
      readonly kind: 'when';
      readonly cond: Readonly<Record<string, unknown>>;
      readonly nodes: readonly StageNode[];
    }
  | {
      readonly kind: 'select';
      readonly on: ConfigRef;
      readonly cases: Readonly<Record<string, readonly StageNode[]>>;
    };

export type TriggerNode =
  | {
      readonly kind: 'push';
      readonly name: string;
      readonly map: (event: unknown) => unknown;
    }
  | {
      readonly kind: 'poll';
      readonly name: string;
      readonly url: string;
      readonly headers: Readonly<Record<string, string>>;
      readonly secrets: readonly Secret[];
      readonly valuePath: string;
      readonly intervalSeconds: number;
      readonly map: (value: string) => unknown;
    }
  | {
      readonly kind: 'webhook';
      readonly name: string;
      readonly verify: WebhookVerify;
      readonly map: (event: WebhookEvent) => unknown;
    };

export interface FailureHandlerNode {
  readonly step: AnyStep;
  /** null = every step. */
  readonly steps: readonly AnyStep[] | null;
}

/** Per-pipeline retention; each field overrides the instance's `Retention:*` setting of the same meaning. */
export interface RetentionPolicy {
  /** A run is never deleted while among its pipeline's newest N runs. */
  keepLatestRuns?: number;
  /** A run is never deleted while younger than this. */
  minRunAgeDays?: number;
  /** An unused compiled binary is deleted this long after it was last modified. */
  buildGraceDays?: number;
}

export interface PipelineData {
  readonly name: string;
  readonly description: string | undefined;
  readonly input: AnyModel;
  readonly retention: RetentionPolicy | undefined;
  readonly nodes: StageNode[];
  readonly triggers: TriggerNode[];
  readonly failureHandlers: FailureHandlerNode[];
  /** Structural problems found while building (reported by describe). */
  readonly problems: string[];
}

export function pipelineData(p: unknown): PipelineData {
  const d =
    typeof p === 'object' && p !== null
      ? (p as Record<symbol, unknown>)[PIPELINE]
      : undefined;
  if (!d)
    throw new TypeError(
      'not a pipeline (the entry point must default-export pipeline({...})...)',
    );
  return d as PipelineData;
}
export function isPipeline(p: unknown): boolean {
  return typeof p === 'object' && p !== null && PIPELINE in p;
}

// ─── Pipeline builder ────────────────────────────────────────────────────────────────────────

// Builder calls record into shared data and hand back a fresh handle; a handle that was already
// extended no longer matches its type (it would claim fewer stages than the data has), so reuse is a problem.
const extended = new WeakSet<object>();
const ReusedProblem =
  'a pipeline value was extended twice: chain the calls (pipeline({...}).stage(a).stage(b)) instead of reusing an earlier value';

export interface PipelineDef<In extends AnyModel> {
  /** Display name. */
  name: string;
  description?: string;
  /** The pipeline input model (what triggers produce, what `when` matches on). */
  input: In;
  /** Retention overrides for this pipeline's runs and binaries (unset fields keep the instance's values). */
  retention?: RetentionPolicy;
}

export class Pipeline<In extends AnyModel, A extends AvailMap> {
  /** Phantom: the availability map accumulated so far. */
  declare readonly __avail: A;
  readonly [PIPELINE]: PipelineData;

  constructor(
    def: PipelineDef<In>,
    shared?: {
      triggers: TriggerNode[];
      failureHandlers: FailureHandlerNode[];
      problems: string[];
    },
  ) {
    this[PIPELINE] = {
      name: def.name,
      description: def.description,
      input: def.input,
      retention: def.retention,
      nodes: [],
      triggers: shared?.triggers ?? [],
      failureHandlers: shared?.failureHandlers ?? [],
      problems: shared?.problems ?? [],
    };
  }

  private next(): never {
    const data = this[PIPELINE];
    if (extended.has(this) && !data.problems.includes(ReusedProblem))
      data.problems.push(ReusedProblem);
    extended.add(this);
    const handle = Object.create(Object.getPrototypeOf(this)) as Pipeline<In, A>;
    (handle as { [PIPELINE]: PipelineData })[PIPELINE] = data;
    return handle as never;
  }

  /** Map a GitHub push to the pipeline input (`z.input` view). Runs as code in a `map-trigger` Job per event. */
  onPush<R extends InferInput<In>>(
    map: (event: PushEvent) => Exact<R, InferInput<In>>,
  ): this {
    this[PIPELINE].triggers.push({
      kind: 'push',
      name: 'push',
      map: map as (event: unknown) => unknown,
    });
    return this.next();
  }

  /**
   * Poll a URL; when the JSON value at `valuePath` changes, map it to the pipeline input.
   * Header values may reference the poll's own declared secrets as `$SECRET:NAME`.
   */
  onPoll<
    R extends InferInput<In>,
    const Sec extends readonly Secret[] = [],
    const H extends Record<string, string> = {},
  >(
    cfg: {
      /** Trigger id, unique per pipeline (default "poll"). Used by `map-trigger <name>`. */
      name?: string;
      url: string;
      secrets?: Sec;
      headers?: { [K in keyof H]: CheckHeader<H[K], Sec[number]['name']> };
      valuePath: string;
      intervalSeconds: number;
    },
    map: (value: string) => Exact<R, InferInput<In>>,
  ): this {
    this[PIPELINE].triggers.push({
      kind: 'poll',
      name: cfg.name ?? 'poll',
      url: cfg.url,
      headers: { ...((cfg.headers ?? {}) as Record<string, string>) },
      secrets: [...(cfg.secrets ?? [])],
      valuePath: cfg.valuePath,
      intervalSeconds: cfg.intervalSeconds,
      map: map as (value: string) => unknown,
    });
    return this.next();
  }

  /**
   * Start a run from a delivery to `/api/hooks/<pipeline id>/<name>`, authenticated by `verify`. Map it to the
   * pipeline input (`z.input` view), or return `null` to ignore it. Runs as code in a `map-trigger` Job per delivery.
   */
  onWebhook<R extends InferInput<In>>(
    cfg: { name: string; verify: WebhookVerify },
    map: (event: WebhookEvent) => Exact<R, InferInput<In>> | null,
  ): this {
    this[PIPELINE].triggers.push({
      kind: 'webhook',
      name: cfg.name,
      verify: cfg.verify,
      map: map as (event: WebhookEvent) => unknown,
    });
    return this.next();
  }

  /**
   * Add a stage (its steps run in parallel). Entries: `step` (inputs matched by model) or
   * `[step, from => ({ … })]` (explicit mapping). Every entry sees only what was available
   * *before* this stage.
   */
  stage<const E extends readonly StageEntry<A>[]>(
    ...entries: CheckStage<E, A>
  ): Pipeline<In, Simplify<A & OutputsOf<E, false>>>;
  stage(...entries: readonly unknown[]): unknown {
    const data = this[PIPELINE];
    const steps: StageStepNode[] = [];
    entries.forEach((e, i) => {
      if (
        Array.isArray(e) &&
        e.length === 2 &&
        isStep(e[0]) &&
        typeof e[1] === 'function'
      ) {
        steps.push({ step: e[0], mapper: e[1] as MapperFunction });
      } else if (isStep(e)) {
        steps.push({ step: e });
      } else if (typeof e === 'function') {
        data.problems.push(
          `stage entry ${i + 1} is a bare function; mappers are given as [step, from => ({ ... })] tuples`,
        );
      } else {
        data.problems.push(
          `stage entry ${i + 1} is neither a step nor a [step, mapper] pair`,
        );
      }
    });
    if (entries.length === 0) data.problems.push('empty stage');
    data.nodes.push({ kind: 'stage', steps });
    return this.next();
  }

  /** Declarative condition on literal-like pipeline-input fields. Outputs produced inside become maybe-available after. */
  when<B extends AvailMap>(
    cond: WhenCond<In>,
    block: (p: Pipeline<In, A>) => Pipeline<In, B>,
  ): Pipeline<In, Simplify<A & MakeMaybe<Omit<B, keyof A>>>> {
    const data = this[PIPELINE];
    const inner = new Pipeline<In, A>({ name: data.name, input: data.input as In }, data);
    const returned = block(inner);
    if (!isPipeline(returned) || pipelineData(returned) !== inner[PIPELINE])
      data.problems.push(
        'a when block must return the pipeline it was given (p => p.stage(...))',
      );
    data.nodes.push({
      kind: 'when',
      cond: { ...(cond as object) },
      nodes: inner[PIPELINE].nodes,
    });
    return this.next();
  }

  /**
   * Branch on config: `on` references a discriminated union in a config entry, and each case gets the
   * pipeline and `on` narrowed to its variant. Exactly one case runs, chosen by the run's config
   * snapshot (not by run input; that is `when`). The same step name may appear in several cases.
   */
  select<U, const C extends SelectCases<In, A, U, DiscriminatorOf<U>>>(
    on: ConfigRef<U> & ([SelectOnError<U>] extends [never] ? unknown : SelectOnError<U>),
    cases: C & {
      readonly [
        K in Exclude<keyof C, keyof SelectCases<In, A, U, DiscriminatorOf<U>>>
      ]: `❌ '${K & string}' is not a case of this select`;
    },
  ): Pipeline<In, AfterSelect<A, CaseAvail<C>>> {
    const data = this[PIPELINE];
    const recorded: Record<string, StageNode[]> = {};
    const union = isConfigRef(on) ? unionAt(on) : undefined;
    if (!union)
      data.problems.push(
        'select: the first argument must reference a z.discriminatedUnion in a config entry',
      );
    for (const [value, fn] of Object.entries(cases as Record<string, unknown>)) {
      if (typeof fn !== 'function') {
        data.problems.push(`select: case '${value}' is not a function`);
        continue;
      }
      const inner = new Pipeline<In, A>(
        { name: data.name, input: data.input as In },
        data,
      );
      const returned = (fn as (p: unknown, ref: unknown) => unknown)(
        inner,
        union ? narrowConfigRef(on, union.discriminator, value) : on,
      );
      if (!isPipeline(returned) || pipelineData(returned) !== inner[PIPELINE])
        data.problems.push(
          `select: case '${value}' must return the pipeline it was given ((p, ref) => p.stage(...))`,
        );
      recorded[value] = inner[PIPELINE].nodes;
    }
    data.nodes.push({ kind: 'select', on, cases: recorded });
    return this.next();
  }

  /** Run `handler` (a step whose inputs are all FailureContext) when one of `steps` (default: any) fails. Untracked, best-effort. */
  onFailure<S extends AnyStep>(
    handler: S &
      ([Exclude<S['input'][keyof S['input']], typeof FailureContext>] extends [never]
        ? unknown
        : "❌ a failure handler's inputs must all be FailureContext"),
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    opts?: { steps?: readonly Step<StepNames<A>, any, any, any, any>[] },
  ): this {
    this[PIPELINE].failureHandlers.push({
      step: handler as AnyStep,
      steps: opts?.steps ? [...(opts.steps as readonly AnyStep[])] : null,
    });
    return this.next();
  }
}

/** Start a pipeline definition. The entry point must default-export the result. */
export function pipeline<In extends AnyModel>(
  def: PipelineDef<In>,
): Pipeline<In, InitialAvail<In>> {
  return new Pipeline(def) as never;
}
