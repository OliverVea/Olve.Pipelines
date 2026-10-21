// Records a mapper function as a path mapping by calling it with recording proxies.
//
// What the proxy CAN catch (it throws): coercion (template strings, `+`, String(), Number()),
// method calls (`.slice()`), spreading / Object.keys / `in`, assignment, JSON.stringify.
// What it CANNOT catch (JS gives no hook): truthiness (`r.x ? a : b`, `&&`, `||`, `!`),
// strict/loose equality against a non-proxy (`r.x === "prod"` is just `false`), `typeof`.
// Those are caught (best-effort) by lintMapperSource() below, and properly only by TypeScript.

const REF = Symbol("olve.ref");

export class MapperError extends Error {}

export type MapExpr =
  | { path: string }
  | { const: unknown }
  | { object: Record<string, MapExpr> };

function refProxy(path: string): any {
  const fail = (what: string): never => {
    throw new MapperError(
      `mapper ${what} on reference '${path}'. Mappers may only use property access and literals ` +
        `(no template strings, arithmetic, method calls or logic) — move computation into the handler.`,
    );
  };
  // Function target so calls hit the `apply` trap.
  const target = function () {};
  return new Proxy(target, {
    get(_t, prop) {
      if (prop === REF) return path;
      if (prop === "then") return undefined; // `await ref` must not recurse
      if (prop === Symbol.toPrimitive || prop === "toString" || prop === "valueOf")
        return fail("coerces a value (template string / concatenation / arithmetic / comparison)");
      if (prop === "toJSON") return fail("serializes a reference (JSON.stringify)");
      if (typeof prop === "symbol") return fail(`uses ${String(prop)} (iteration/spread/inspection)`);
      return refProxy(`${path}.${prop}`);
    },
    apply: () => fail("calls a function/method"),
    construct: () => fail("constructs"),
    set: () => fail("assigns"),
    deleteProperty: () => fail("deletes"),
    has: () => fail("uses the `in` operator"),
    ownKeys: () => fail("enumerates/spreads"),
    defineProperty: () => fail("defines a property"),
    getPrototypeOf: () => fail("inspects the prototype (instanceof)"),
  });
}

/** The root `r` passed to a mapper: `r.input`, `r.<stepName>`. */
export function rootProxy(allowedRoots?: Set<string>): any {
  return new Proxy(
    {},
    {
      get(_t, prop) {
        if (typeof prop === "symbol" || prop === "then") return undefined;
        if (allowedRoots && !allowedRoots.has(prop))
          throw new MapperError(`mapper references '${prop}', which is not available at this stage ` +
            `(available: ${[...allowedRoots].join(", ") || "none"})`);
        return refProxy(prop);
      },
      ownKeys: () => { throw new MapperError("mapper enumerates/spreads the root"); },
    },
  );
}

function toExpr(v: unknown, at: string): MapExpr {
  if (typeof v === "function") {
    const path = (v as any)[REF];
    if (typeof path === "string") return { path };
    throw new MapperError(`mapper returns a function at '${at}'`);
  }
  if (v === null || typeof v === "string" || typeof v === "number" || typeof v === "boolean")
    return { const: v };
  if (Array.isArray(v)) {
    const items = v.map((x, i) => toExpr(x, `${at}[${i}]`));
    if (items.every((e) => "const" in e)) return { const: items.map((e) => (e as any).const) };
    throw new MapperError(`mapper puts references inside an array at '${at}' (unsupported in the spike)`);
  }
  if (typeof v === "object") {
    const out: Record<string, MapExpr> = {};
    for (const [k, x] of Object.entries(v as object)) out[k] = toExpr(x, at ? `${at}.${k}` : k);
    return { object: out };
  }
  throw new MapperError(`mapper returns unsupported value (${typeof v}) at '${at}'`);
}

/** Calls `fn` with recording proxies and returns `{ inputName: MapExpr }`. */
export function recordMapper(fn: (r: any) => unknown, allowedRoots?: Set<string>): Record<string, MapExpr> {
  lintMapperSource(fn);
  const result = fn(rootProxy(allowedRoots));
  if (result instanceof Promise) throw new MapperError("mapper must be synchronous");
  const expr = toExpr(result, "");
  if (!("object" in expr)) throw new MapperError("mapper must return an object literal");
  return expr.object;
}

/**
 * Best-effort source check for what the proxy cannot see (truthiness / equality / ternaries).
 * Relies on Function.prototype.toString(), which survives `bun build --compile` as long as
 * the build is not minified (verified in the spike). Strips string literals first.
 */
export function lintMapperSource(fn: Function): void {
  const src = fn.toString();
  if (src.includes("`")) throw new MapperError(`mapper uses a template string: ${src}`);
  const stripped = src.replace(/"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'/g, '""');
  const body = stripped.replace(/^[^=]*=>/, ""); // drop the parameter list + arrow
  const banned: [RegExp, string][] = [
    [/\?(?![.?])/, "a ternary"],
    [/[=!]==?/, "an equality comparison"],
    [/&&|\|\||\?\?/, "a logical operator"],
    [/(^|[^=!<>])!(?!=)/, "negation"],
    [/\b(if|switch|for|while|typeof|new|await)\b/, "control flow / operators"],
    [/[+*/%]|[^=]-(?!>)|<|>/, "arithmetic/comparison"],
  ];
  for (const [re, what] of banned)
    if (re.test(body)) throw new MapperError(`mapper contains ${what} — only property access and literals are allowed: ${src.trim()}`);
}

/** Path of a recorded reference, or undefined (for tests). */
export const refPath = (v: unknown): string | undefined =>
  typeof v === "function" ? ((v as any)[REF] as string | undefined) : undefined;
