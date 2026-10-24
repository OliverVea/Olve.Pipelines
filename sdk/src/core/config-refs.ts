// Config references inside library-step definitions: collected (the step receives their entries),
// recorded in the manifest as `{"$config": "<entry>.<path>"}`, and resolved from ctx.config at run time.
import { configRefPath, configRefTarget, isConfigRef, type AnyConfigEntry, type ConfigRef } from "./config.ts";

/** `T` with every config reference replaced by the type of its value. */
export type Resolved<T> = T extends ConfigRef<infer U>
  ? U
  : T extends readonly (infer E)[]
    ? Resolved<E>[]
    : T extends (...args: never[]) => unknown
      ? T
      : T extends object
        ? { [K in keyof T]: Resolved<T[K]> }
        : T;

/** Manifest key of a recorded config reference. */
export const CONFIG_REF_KEY = "$config";
/** Beside it on a reference narrowed by a `select` case: the case's condition, `<ref>.<discriminator>=<value>`. */
export const CONFIG_CASE_KEY = "$case";

function isPlain(v: unknown): v is Record<string, unknown> {
  if (typeof v !== "object" || v === null || Array.isArray(v)) return false;
  const proto = Object.getPrototypeOf(v);
  return proto === Object.prototype || proto === null;
}

/** Every config entry referenced anywhere in `v` (plain objects and arrays only), by name. */
export function configRefsIn(v: unknown, into = new Map<string, AnyConfigEntry>()): AnyConfigEntry[] {
  if (isConfigRef(v)) {
    const { entry } = configRefTarget(v);
    if (!into.has(entry.name)) into.set(entry.name, entry);
  } else if (Array.isArray(v)) for (const x of v) configRefsIn(x, into);
  else if (isPlain(v)) for (const x of Object.values(v)) configRefsIn(x, into);
  return [...into.values()];
}

/** `v` as manifest JSON: references become `{"$config": "target.beta.host"}`; literal keys starting with `$` are reserved. */
export function recordConfigRefs(v: unknown): unknown {
  if (isConfigRef(v)) {
    const { path, case: kase } = configRefTarget(v);
    for (const seg of path) if (seg === "" || seg.includes(".")) throw new Error(`config reference '${configRefPath(v)}': field names must not be empty or contain '.'`);
    return { [CONFIG_REF_KEY]: configRefPath(v), ...(kase ? { [CONFIG_CASE_KEY]: kase } : {}) };
  }
  if (Array.isArray(v)) return v.map(recordConfigRefs);
  if (isPlain(v)) {
    const out: Record<string, unknown> = {};
    for (const [k, x] of Object.entries(v)) {
      if (k.startsWith("$")) throw new Error(`key '${k}': keys starting with '$' are reserved`);
      out[k] = recordConfigRefs(x);
    }
    return out;
  }
  return v;
}

/** `v` with every reference replaced by its value in `config` (the step's parsed config entries). */
export function resolveConfigRefs<T>(v: T, config: Readonly<Record<string, unknown>>): Resolved<T> {
  if (isConfigRef(v)) {
    const { entry, path } = configRefTarget(v);
    if (!Object.hasOwn(config, entry.name)) throw new Error(`config entry '${entry.name}' was not delivered to the step`);
    let cur: unknown = config[entry.name];
    for (const seg of path) cur = cur !== null && typeof cur === "object" ? (cur as Record<string, unknown>)[seg] : undefined;
    return cur as Resolved<T>;
  }
  if (Array.isArray(v)) return v.map((x) => resolveConfigRefs(x, config)) as Resolved<T>;
  if (isPlain(v)) {
    const out: Record<string, unknown> = {};
    for (const [k, x] of Object.entries(v)) out[k] = resolveConfigRefs(x, config);
    return out as Resolved<T>;
  }
  return v as Resolved<T>;
}

/** Literal `v` (no reference inside), so define-time checks can run on it. */
export function isLiteral(v: unknown): boolean {
  return configRefsIn(v).length === 0;
}
