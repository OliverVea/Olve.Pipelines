export type Primitive = string | number | boolean | bigint | null | undefined;
export type Simplify<T> = { [K in keyof T]: T[K] } & {};
export type Awaitable<T> = T | Promise<T>;
export type IsUnion<T, U = T> = T extends unknown
  ? [U] extends [T]
    ? false
    : true
  : never;
/** `Ok` when there is no error, else the error (a readable string-literal type). */
export type OrError<Err, Ok> = [Err] extends [never] ? Ok : Err;
