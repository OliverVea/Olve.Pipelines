import { isModel, type AnyModel } from "./models.ts";

/** Who or what may supply an external input's value. */
export type ExternalSource = { readonly kind: "person" };

/** A signed-in user answers, in the UI or with `pl input`. */
export function person(): ExternalSource {
  return Object.freeze({ kind: "person" as const });
}

/**
 * A step input whose value comes from outside the run. The step waits until it is answered (the value is
 * validated against `model`), then runs with the value in its input like any other.
 */
export interface External<M extends AnyModel = AnyModel> {
  readonly kind: "external";
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

export function external<M extends AnyModel>(model: M, options: ExternalOptions): External<M> {
  return Object.freeze({
    kind: "external" as const,
    model,
    title: options.title,
    description: options.description ?? "",
    from: Object.freeze([...(options.from ?? [person()])]),
  });
}

export function isExternal(v: unknown): v is External {
  return typeof v === "object" && v !== null && (v as { kind?: unknown }).kind === "external" && isModel((v as { model?: unknown }).model);
}
