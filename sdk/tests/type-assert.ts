// Tiny type-level assertion helpers for compile-pass tests.
export type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
export type Expect<T extends true> = T;
/** The availability map (`in` keys) a builder has accumulated. */
export type AvailOf<P> = P extends { readonly __avail: infer A } ? A : never;
