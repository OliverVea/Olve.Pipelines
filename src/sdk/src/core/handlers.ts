import {
  IMPLEMENTATION,
  SKIPPED,
  type ImplementationBrand,
  type SkippedBrand,
} from './brands.ts';
import type { StepCtx } from '../ctx/types.ts';
import type {
  AnyStep,
  CellOf,
  ConfigOf,
  OutputValue,
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

/** A step with its handler. It is itself a step: stage it like any value. */
export type Implementation<S extends AnyStep = AnyStep> = S & {
  readonly [ImplementationBrand]: true;
  /** The step as defined. */
  readonly step: S;
  readonly handler: Handler<S>;
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
): Implementation<S> {
  if (isImplementation(step))
    throw new TypeError(
      `step '${(step as AnyStep).name}' already carries its implementation`,
    );
  return Object.freeze({
    ...step,
    [IMPLEMENTATION]: true,
    step,
    handler: handler as unknown as Handler<S>,
  }) as unknown as Implementation<S>;
}

export function isImplementation(v: unknown): v is Implementation {
  return (
    typeof v === 'object' &&
    v !== null &&
    (v as Record<symbol, unknown>)[IMPLEMENTATION] === true
  );
}
