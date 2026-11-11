import {
  IMPLEMENTATION,
  SKIPPED,
  type ImplementationBrand,
  type SkippedBrand,
} from './brands.ts';
import type { StepCtx } from '../ctx/types.ts';
import { isExternal, type External } from './external.ts';
import type {
  AnyStep,
  CellOf,
  ConfigOf,
  OutputValue,
  RegularInputKey,
  SecretsOf,
  StepInput,
} from './steps.ts';
import type { Awaitable } from './types.ts';

/** Returned by `ctx.skip(reason)`: the step produced no output on purpose. */
export interface Skipped {
  readonly [SkippedBrand]: true;
  readonly reason: string;
}
export function skipped(reason: string): Skipped {
  return Object.freeze({ [SKIPPED]: true, reason }) as unknown as Skipped;
}
export function isSkipped(v: unknown): v is Skipped {
  return (
    typeof v === 'object' &&
    v !== null &&
    (v as Record<symbol, unknown>)[SKIPPED] === true
  );
}

export type HandlerResult<S extends AnyStep> = OutputValue<S> | Skipped;
/** The ctx a handler of `S` gets. */
export type CtxOf<S extends AnyStep> = StepCtx<SecretsOf<S>, ConfigOf<S>, CellOf<S>>;
export type Handler<S extends AnyStep> = (
  input: StepInput<S['input']>,
  ctx: CtxOf<S>,
) => Awaitable<HandlerResult<S>>;

/** What a preflight receives: the step's non-external inputs (the external values don't exist yet). */
export type PreflightInput<S extends AnyStep> = StepInput<
  Pick<S['input'], RegularInputKey<S['input']>>
>;
/** The ctx a preflight of `S` gets: the handler's, without output files and `skip` (the step still waits). */
export type PreflightCtxOf<S extends AnyStep> = Omit<CtxOf<S>, 'output' | 'skip'>;
export type Preflight<S extends AnyStep> = (
  input: PreflightInput<S>,
  ctx: PreflightCtxOf<S>,
) => Awaitable<void>;

/** True when some input of the step is `external(...)`. */
type HasExternalInput<I> = true extends {
  [K in keyof I]-?: I[K] extends External ? true : false;
}[keyof I]
  ? true
  : false;

export interface ImplementOptions<S extends AnyStep> {
  /**
   * Runs before the step starts waiting for its external inputs, e.g. to hand work and
   * `ctx.inputUrl(...)` to the system that will answer. Throwing fails the attempt without waiting.
   */
  preflight?: Preflight<S>;
}

/** A step with its handler. It is itself a step: stage it like any value. */
export type Implementation<S extends AnyStep = AnyStep> = S & {
  readonly [ImplementationBrand]: true;
  /** The step as defined. */
  readonly step: S;
  readonly handler: Handler<S>;
  /** Runs before the step waits for its external inputs; absent when none. */
  readonly preflight?: Preflight<S>;
};

/** Block-bodied/async returns get no excess-property check; this restores it for handler outputs. */
type NoExtraFields<R, T> = R extends Skipped | void
  ? unknown
  : {
      [
        K in Exclude<keyof R, keyof T>
      ]: `❌ '${K & string}' is not a field of the output model`;
    };

/**
 * Implement a step. Input, ctx and return types come from the step definition alone. Stage the
 * result directly; a reusable step gives itself a `kind`.
 */
export function implement<S extends AnyStep, R extends HandlerResult<S>>(
  step: S &
    (S extends { readonly [ImplementationBrand]: true }
      ? '❌ this step already carries its implementation'
      : unknown),
  handler: (
    input: StepInput<S['input']>,
    ctx: CtxOf<S>,
  ) => Awaitable<R & NoExtraFields<R, OutputValue<S>>>,
  options?: HasExternalInput<S['input']> extends true
    ? ImplementOptions<S>
    : '❌ a preflight needs a step with an external(...) input',
): Implementation<S> {
  if (isImplementation(step))
    throw new TypeError(
      `step '${(step as AnyStep).name}' already carries its implementation`,
    );
  const preflight =
    typeof options === 'object' ? (options as ImplementOptions<S>).preflight : undefined;
  if (
    preflight !== undefined &&
    !Object.values((step as AnyStep).input as Record<string, unknown>).some(isExternal)
  )
    throw new TypeError(
      `step '${(step as AnyStep).name}': a preflight needs a step with an external(...) input`,
    );
  return Object.freeze({
    ...step,
    [IMPLEMENTATION]: true,
    step,
    handler: handler as unknown as Handler<S>,
    ...(preflight !== undefined ? { preflight } : {}),
  }) as unknown as Implementation<S>;
}

export function isImplementation(v: unknown): v is Implementation {
  return (
    typeof v === 'object' &&
    v !== null &&
    (v as Record<symbol, unknown>)[IMPLEMENTATION] === true
  );
}
