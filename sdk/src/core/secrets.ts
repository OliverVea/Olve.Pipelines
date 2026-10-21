/** A named pipeline secret. Steps (and poll triggers) declare the secrets they use; only those are mounted. */
export interface Secret<N extends string = string> {
  readonly kind: "secret";
  readonly name: N;
  readonly description: string;
}
/** Secret names double as environment variable names in the step container. */
export const SECRET_NAME = /^[A-Z_][A-Z0-9_]*$/;

export function secret<const N extends string>(name: N, description: string): Secret<N> {
  return Object.freeze({ kind: "secret" as const, name, description });
}
export type DeclaredSecrets<N extends string> = { readonly [K in N]: string };
