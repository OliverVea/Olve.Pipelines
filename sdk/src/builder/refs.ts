import type { RefBrand } from "../core/brands.ts";
import type { AnyStep, InferEntry } from "../core/steps.ts";
import type { Primitive } from "../core/types.ts";

/**
 * A typed reference into the run pool — what mappers see. `symbol &` makes template strings, `+`
 * and `===` against values compile errors (TS2731 / TS2469 / TS2367); only property access and
 * literals remain. Fields of a maybe-undefined ref are themselves maybe-undefined.
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
