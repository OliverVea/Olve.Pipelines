import { z } from "zod";
import type { Secret } from "./secrets.ts";

/** A named, typed value stored on the instance (one YAML document per pipeline), declared by the steps that use it. */
export interface ConfigEntry<N extends string = string, S extends z.ZodType = z.ZodType> {
  readonly kind: "config";
  readonly name: N;
  readonly description: string;
  readonly schema: S;
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type AnyConfigEntry = ConfigEntry<string, any>;

/** Config names are YAML keys: camelCase identifiers. */
export const CONFIG_NAME = /^[a-z][a-zA-Z0-9]*$/;

export function config<const N extends string, S extends z.ZodType>(name: N, description: string, schema: S): ConfigEntry<N, S> {
  return Object.freeze({ kind: "config" as const, name, description, schema });
}

export function isConfigEntry(v: unknown): v is AnyConfigEntry {
  return typeof v === "object" && v !== null && (v as { kind?: unknown }).kind === "config" && (v as { schema?: unknown }).schema instanceof z.ZodType;
}

export const SECRET_REF_KIND = "secretRef";

/** A config field that names a secret: `{ secret: NAME }` in YAML. The value never enters the config. */
export function secretRef<const N extends string>(s: Secret<N>) {
  return z.strictObject({ secret: z.literal(s.name) }).meta({ "x-olve-kind": SECRET_REF_KIND });
}

/** `ctx.config`: exactly the entries a step declares, after parsing (defaults applied). */
export type ConfigValues<C extends AnyConfigEntry> = { readonly [E in C as E["name"]]: z.output<E["schema"]> };
