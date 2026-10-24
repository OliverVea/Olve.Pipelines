import { z } from "zod";
import type { Secret } from "./secrets.ts";

const CONFIG_REF = Symbol.for("olve.configRef");
declare const VALUE: unique symbol;

interface RefTarget {
  readonly entry: AnyConfigEntry;
  readonly path: readonly string[];
}

/**
 * A typed reference to a config value (`TARGET.beta.host`), accepted by library steps in place of a
 * literal. It is recorded in the manifest as `{"$config": "target.beta.host"}` and resolved from the
 * run's config when the step runs; the step receives the entry automatically.
 */
export interface ConfigRef<T = unknown> {
  readonly [CONFIG_REF]: RefTarget;
  readonly [VALUE]?: T;
}

/** A literal or a reference to a config value of the same type. */
export type Configurable<T> = T | ConfigRef<T>;

type Fields<T> = T extends readonly unknown[] ? never : T extends object ? T : never;
type Children<T> = [Fields<NonNullable<T>>] extends [never]
  ? {}
  : { readonly [K in keyof Fields<NonNullable<T>> & string]-?: ConfigNode<Fields<NonNullable<T>>[K] | (undefined extends T ? undefined : never)> };
/** A reference to a config value, and to each of its fields. */
export type ConfigNode<T> = ConfigRef<T> & Children<T>;

/** Fields of an entry that are its own properties; reach config fields with these names through `$`. */
export type ReservedConfigField = "kind" | "name" | "description" | "schema" | "$";

/** A named, typed value stored on the instance (one YAML document per pipeline), declared by the steps that use it. */
export interface ConfigEntryBase<N extends string = string, S extends z.ZodType = z.ZodType> {
  readonly kind: "config";
  readonly name: N;
  readonly description: string;
  readonly schema: S;
}
export type ConfigEntry<N extends string = string, S extends z.ZodType = z.ZodType> = ConfigEntryBase<N, S> &
  ConfigRef<z.output<S>> &
  Omit<Children<z.output<S>>, ReservedConfigField> & { readonly $: ConfigNode<z.output<S>> };
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type AnyConfigEntry = ConfigEntryBase<string, any>;

/** Config names are YAML keys: camelCase identifiers. */
export const CONFIG_NAME = /^[a-z][a-zA-Z0-9]*$/;

const NOT_REFS = new Set<PropertyKey>(["then", "toJSON", "constructor", "valueOf", "toString", "asymmetricMatch", "$$typeof", "nodeType"]);

function node(entry: AnyConfigEntry, path: readonly string[]): unknown {
  const target = Object.freeze({ [CONFIG_REF]: Object.freeze({ entry, path }) });
  return new Proxy(target, {
    get(t, prop) {
      if (prop === CONFIG_REF) return t[CONFIG_REF];
      if (typeof prop === "symbol" || NOT_REFS.has(prop)) return undefined;
      return node(entry, [...path, prop]);
    },
  });
}

export function config<const N extends string, S extends z.ZodType>(name: N, description: string, schema: S): ConfigEntry<N, S> {
  const ref = { entry: undefined as unknown as AnyConfigEntry, path: [] as readonly string[] };
  const target = Object.freeze({ kind: "config" as const, name, description, schema, [CONFIG_REF]: ref });
  const entry: unknown = new Proxy(target, {
    get(t, prop) {
      if (prop in t) return t[prop as keyof typeof t];
      if (typeof prop === "symbol" || NOT_REFS.has(prop)) return undefined;
      if (prop === "$") return node(entry as AnyConfigEntry, []);
      return node(entry as AnyConfigEntry, [prop]);
    },
  });
  ref.entry = entry as AnyConfigEntry;
  Object.freeze(ref);
  return entry as ConfigEntry<N, S>;
}

export function isConfigEntry(v: unknown): v is AnyConfigEntry {
  return typeof v === "object" && v !== null && (v as { kind?: unknown }).kind === "config" && (v as { schema?: unknown }).schema instanceof z.ZodType;
}

export function isConfigRef(v: unknown): v is ConfigRef {
  return typeof v === "object" && v !== null && (v as Record<symbol, unknown>)[CONFIG_REF] !== undefined;
}

/** The entry and field path a reference points at. */
export function configRefTarget(ref: ConfigRef): { entry: AnyConfigEntry; path: readonly string[] } {
  return ref[CONFIG_REF];
}

/** `target.beta.host` */
export function configRefPath(ref: ConfigRef): string {
  const { entry, path } = configRefTarget(ref);
  return [entry.name, ...path].join(".");
}

export const SECRET_REF_KIND = "secretRef";

/** A config field that names a secret: `{ secret: NAME }` in YAML. The value never enters the config. */
export function secretRef<const N extends string>(s: Secret<N>) {
  return z.strictObject({ secret: z.literal(s.name) }).meta({ "x-olve-kind": SECRET_REF_KIND });
}

/** `ctx.config`: exactly the entries a step declares, after parsing (defaults applied). */
export type ConfigValues<C extends AnyConfigEntry> = { readonly [E in C as E["name"]]: z.output<E["schema"]> };
