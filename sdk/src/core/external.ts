import { isModel, type AnyModel } from './models.ts';

/** Who or what may supply an external input's value. */
export type ExternalSource =
  | { readonly kind: 'person' }
  | { readonly kind: 'callback' }
  | {
      readonly kind: 'poll';
      readonly url: string;
      readonly valuePath: string;
      readonly intervalSeconds: number;
    };

/** A signed-in user answers, in the UI or with `pl input`. */
export function person(): ExternalSource {
  return Object.freeze({ kind: 'person' as const });
}

/**
 * Another system answers by POSTing to this run's callback URL for the input, which an earlier step hands it
 * with `ctx.inputUrl(step, input)`. The body is the value as JSON, or `{"$decline": "reason"}`.
 */
export function callback(): ExternalSource {
  return Object.freeze({ kind: 'callback' as const });
}

export interface PollOptions {
  /** GET while the step waits; `{run.id}` and `{run.number}` are filled in. */
  url: string;
  /** Dot path to the value in the JSON response; `.` is the whole body. */
  valuePath: string;
  /** Default 30; at least 10. */
  intervalSeconds?: number;
}

/** The service polls `url` while the step waits and answers with the value at `valuePath` once it validates. */
export function poll(options: PollOptions): ExternalSource {
  return Object.freeze({
    kind: 'poll' as const,
    url: options.url,
    valuePath: options.valuePath,
    intervalSeconds: options.intervalSeconds ?? 30,
  });
}

/**
 * A step input whose value comes from outside the run. The step waits until it is answered (the value is
 * validated against `model`), then runs with the value in its input like any other.
 */
export interface External<M extends AnyModel = AnyModel> {
  readonly kind: 'external';
  readonly model: M;
  readonly title: string;
  readonly description: string;
  readonly from: readonly ExternalSource[];
}

export interface ExternalOptions {
  /** What is asked, shown to whoever answers. */
  title: string;
  description?: string;
  /** Who may answer (default: a person). */
  from?: readonly ExternalSource[];
}

export function external<M extends AnyModel>(
  model: M,
  options: ExternalOptions,
): External<M> {
  return Object.freeze({
    kind: 'external' as const,
    model,
    title: options.title,
    description: options.description ?? '',
    from: Object.freeze([...(options.from ?? [person()])]),
  });
}

export function isExternal(v: unknown): v is External {
  return (
    typeof v === 'object' &&
    v !== null &&
    (v as { kind?: unknown }).kind === 'external' &&
    isModel((v as { model?: unknown }).model)
  );
}
