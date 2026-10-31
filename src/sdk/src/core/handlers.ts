import {
  IMPLEMENTATION,
  SKIPPED,
  type ImplementationBrand,
  type SkippedBrand,
} from './brands.ts';
import type { Ctx } from '../ctx/types.ts';
import type { AnyStep, ConfigOf, OutputValue, SecretsOf, StepInput } from './steps.ts';
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
export type Handler<S extends AnyStep> = (
  input: StepInput<S['input']>,
  ctx: Ctx<SecretsOf<S>, ConfigOf<S>>,
) => Awaitable<HandlerResult<S>>;

/** A step with its handler. It is itself a step: stage it directly, or default-export it from `handlers/*.ts`. */
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
 * Implement a step. Input, ctx and return types come from the step definition alone. Default-export
 * the result from a `handlers/*.ts` file, or stage it directly (a reusable step: give it a `kind`).
 */
export function implement<S extends AnyStep, R extends HandlerResult<S>>(
  step: S &
    (S extends { readonly [ImplementationBrand]: true }
      ? '❌ this step already carries its implementation'
      : unknown),
  handler: (
    input: StepInput<S['input']>,
    ctx: Ctx<SecretsOf<S>, ConfigOf<S>>,
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
