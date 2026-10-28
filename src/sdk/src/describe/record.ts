// Records a mapper as a tagged mapping by calling it with recording proxies.
//
// The proxy throws on coercion (template strings, `+`, String()), method calls, spreading,
// `in`, assignment and JSON.stringify. It CANNOT see truthiness, `!`, `??`, `||`, ternaries or
// `===` against a value — those are rejected by the static source analysis (mapper-source.ts),
// and primarily by the `Ref<T>` types.
import { REF_PATH } from '../core/brands.ts';

export class MapperError extends Error {
  override name = 'MapperError';
}

/** Manifest mapping node: `{ref}` | `{const}` | a nested object of mappings. */
export type Mapping = { ref: string } | { const: unknown } | { [key: string]: Mapping };

function refProxy(path: string): unknown {
  const fail = (what: string): never => {
    throw new MapperError(
      `mapper ${what} on reference '${path}'; mappers may only use property access and literals ` +
        `(no template strings, arithmetic, method calls or logic) — move computation into the handler`,
    );
  };
  const target = function () {}; // function target so calls hit the `apply` trap
  return new Proxy(target, {
    get(_t, prop) {
      if (prop === REF_PATH) return path;
      if (prop === 'then') return undefined; // `await ref` must not recurse
      if (prop === Symbol.toPrimitive || prop === 'toString' || prop === 'valueOf')
        return fail(
          'coerces a value (template string / concatenation / arithmetic / comparison)',
        );
      if (prop === 'toJSON') return fail('serializes a reference (JSON.stringify)');
      if (typeof prop === 'symbol')
        return fail(`uses ${String(prop)} (iteration/spread/inspection)`);
      if (prop.includes('.'))
        return fail(
          `accesses field '${prop}' (field names containing '.' cannot be mapped)`,
        );
      return refProxy(`${path}.${prop}`);
    },
    apply: () => fail('calls a function/method'),
    construct: () => fail('constructs'),
    set: () => fail('assigns'),
    deleteProperty: () => fail('deletes'),
    has: () => fail('uses the `in` operator'),
    ownKeys: () => fail('enumerates/spreads'),
    defineProperty: () => fail('defines a property'),
    getPrototypeOf: () => fail('inspects the prototype (instanceof)'),
  });
}

/** The `from` argument: `from.input`, `from.<step-name>`. */
export function rootProxy(available: ReadonlySet<string>): unknown {
  return new Proxy(
    {},
    {
      get(_t, prop) {
        if (typeof prop === 'symbol' || prop === 'then') return undefined;
        if (!available.has(prop))
          throw new MapperError(
            `mapper references '${prop}', which is not available at this stage (available: ${[...available].sort().join(', ') || 'none'})`,
          );
        return refProxy(prop);
      },
      has: () => {
        throw new MapperError('mapper uses the `in` operator on its argument');
      },
      ownKeys: () => {
        throw new MapperError('mapper enumerates/spreads its argument');
      },
    },
  );
}

export const refPathOf = (v: unknown): string | undefined =>
  typeof v === 'function'
    ? ((v as unknown as Record<symbol, unknown>)[REF_PATH] as string | undefined)
    : undefined;

function toMapping(v: unknown, at: string): Mapping {
  const path = refPathOf(v);
  if (path !== undefined) return { ref: path };
  if (typeof v === 'function')
    throw new MapperError(`mapper returns a function at '${at}'`);
  if (v === undefined) throw new MapperError(`mapper returns undefined at '${at}'`);
  if (v === null || typeof v === 'string' || typeof v === 'boolean') return { const: v };
  if (typeof v === 'number') {
    if (!Number.isFinite(v))
      throw new MapperError(`mapper returns a non-finite number at '${at}'`);
    return { const: v };
  }
  if (Array.isArray(v)) {
    const items = v.map((x, i) => toMapping(x, `${at}[${i}]`));
    if (items.every((e) => 'const' in e && Object.keys(e).length === 1))
      return { const: items.map((e) => (e as { const: unknown }).const) };
    throw new MapperError(
      `mapper puts references inside an array at '${at}' (only constant arrays are supported)`,
    );
  }
  if (typeof v === 'object') {
    if (
      Object.getPrototypeOf(v) !== Object.prototype &&
      Object.getPrototypeOf(v) !== null
    )
      throw new MapperError(`mapper returns a non-plain object at '${at}'`);
    const keys = Object.keys(v);
    if (keys.length === 1 && (keys[0] === 'ref' || keys[0] === 'const'))
      throw new MapperError(
        `mapper builds an object whose only field is '${keys[0]}' at '${at}'; that is reserved by the manifest's mapping encoding — map the whole object with a ref instead`,
      );
    const out: Record<string, Mapping> = {};
    for (const k of keys)
      out[k] = toMapping((v as Record<string, unknown>)[k], at ? `${at}.${k}` : k);
    return out;
  }
  throw new MapperError(`mapper returns an unsupported value (${typeof v}) at '${at}'`);
}

/** Calls `fn` with recording proxies and returns `{ inputKey: Mapping }`. */
export function recordMapper(
  fn: (from: unknown) => unknown,
  available: ReadonlySet<string>,
): Record<string, Mapping> {
  const result = fn(rootProxy(available));
  if (
    refPathOf(result) !== undefined ||
    typeof result !== 'object' ||
    result === null ||
    Array.isArray(result)
  )
    throw new MapperError('mapper must return an object literal');
  if (result instanceof Promise) throw new MapperError('mapper must be synchronous');
  const out: Record<string, Mapping> = {};
  for (const k of Object.keys(result))
    out[k] = toMapping((result as Record<string, unknown>)[k], k);
  return out;
}
