import { SECRET_REF_KIND } from '../core/config.ts';
import { CONFIG_CASE_KEY, CONFIG_REF_KEY } from '../core/config-refs.ts';
import type { JsonSchema } from './schemas.ts';

/**
 * The schemas at `path` inside an entry's schema (through properties and records): every union branch that has the field, except where
 * `kase` (`<entry>.<path>.<discriminator>=<value>`) narrows a union to one branch.
 */
export function subSchemas(
  schema: unknown,
  path: readonly string[],
  kase: string | undefined,
  entry: string,
): unknown[] {
  let narrow: { at: string; discriminator: string; value: string } | undefined;
  if (kase) {
    const eq = kase.lastIndexOf('=');
    const left = kase.slice(0, eq);
    const dot = left.lastIndexOf('.');
    narrow = {
      at: left.slice(0, dot),
      discriminator: left.slice(dot + 1),
      value: kase.slice(eq + 1),
    };
  }
  const walk = (node: unknown, rest: readonly string[], here: string): unknown[] => {
    if (node === null || typeof node !== 'object') return [];
    const o = node as JsonSchema;
    const branches = (o.oneOf ?? o.anyOf) as JsonSchema[] | undefined;
    if (Array.isArray(branches)) {
      const chosen =
        narrow && narrow.at === here
          ? branches.filter(
              (b) =>
                (
                  (b.properties as Record<string, JsonSchema> | undefined)?.[
                    narrow!.discriminator
                  ] as JsonSchema | undefined
                )?.const === narrow!.value,
            )
          : branches;
      return chosen.flatMap((b) => walk(b, rest, here));
    }
    if (rest.length === 0) return [node];
    const props = o.properties as Record<string, JsonSchema> | undefined;
    if (props && Object.hasOwn(props, rest[0]!))
      return walk(props[rest[0]!], rest.slice(1), `${here}.${rest[0]}`);
    if (o.additionalProperties && typeof o.additionalProperties === 'object')
      return walk(o.additionalProperties, rest.slice(1), `${here}.${rest[0]}`);
    return [];
  };
  return walk(schema, path, entry);
}

/** Secret names of every secretRef inside a config entry's JSON Schema. */
export function secretRefsOf(schema: unknown): string[] {
  const out = new Set<string>();
  const walk = (v: unknown) => {
    if (Array.isArray(v)) return v.forEach(walk);
    if (v === null || typeof v !== 'object') return;
    const o = v as JsonSchema;
    if (o['x-olve-kind'] === SECRET_REF_KIND) {
      const name = (o.properties as { secret?: { const?: unknown } } | undefined)?.secret
        ?.const;
      if (typeof name === 'string') out.add(name);
    }
    Object.values(o).forEach(walk);
  };
  walk(schema);
  return [...out].sort();
}

/** The `{"$config": "<entry>.<path>"[, "$case": …]}` references in a step's recorded settings. */
export function recordedConfigRefs(
  v: unknown,
  out: { ref: string; case?: string }[] = [],
): { ref: string; case?: string }[] {
  if (Array.isArray(v)) v.forEach((x) => recordedConfigRefs(x, out));
  else if (v !== null && typeof v === 'object') {
    const o = v as Record<string, unknown>;
    const keys = Object.keys(o).sort();
    const isRef =
      typeof o[CONFIG_REF_KEY] === 'string' &&
      (keys.length === 1
        ? keys[0] === CONFIG_REF_KEY
        : keys.length === 2 &&
          keys[0] === CONFIG_CASE_KEY &&
          keys[1] === CONFIG_REF_KEY &&
          typeof o[CONFIG_CASE_KEY] === 'string');
    if (isRef)
      out.push({
        ref: o[CONFIG_REF_KEY] as string,
        ...(keys.length === 2 ? { case: o[CONFIG_CASE_KEY] as string } : {}),
      });
    else Object.values(o).forEach((x) => recordedConfigRefs(x, out));
  }
  return out;
}
