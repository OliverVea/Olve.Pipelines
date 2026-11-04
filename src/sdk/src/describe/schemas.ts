import { z } from 'zod';
import { canonicalize } from '../util/canonical.ts';
import type { ModelSchemasT } from './manifest-schema.ts';

export type JsonSchema = Record<string, unknown>;

function stripStrict(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(stripStrict);
  if (v !== null && typeof v === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, x] of Object.entries(v))
      if (!(k === 'additionalProperties' && x === false)) out[k] = stripStrict(x);
    return out;
  }
  return v;
}

/** JSON Schema views of a zod schema. Throws if it is not representable (dates, transforms, …). */
export function schemaViews(schema: z.ZodType): ModelSchemasT {
  const view = (io: 'input' | 'output') => {
    const js = z.toJSONSchema(schema, { io, target: 'draft-2020-12' }) as JsonSchema;
    delete js.$schema; // constant noise; apiVersion pins the dialect
    return canonicalize(js) as JsonSchema;
  };
  const output = view('output');
  const input = view('input');
  const same = JSON.stringify(stripStrict(output)) === JSON.stringify(input);
  return same ? { output } : { output, input };
}

/** Whether `segments` lead to a field of a JSON Schema. */
export function resolveField(
  schema: JsonSchema | undefined,
  segments: readonly string[],
): boolean {
  if (!schema) return false;
  if (segments.length === 0) return true;
  const [seg, ...rest] = segments as [string, ...string[]];
  if (Array.isArray(schema.anyOf))
    return (schema.anyOf as JsonSchema[]).some((b) => resolveField(b, segments));
  if (Array.isArray(schema.oneOf))
    return (schema.oneOf as JsonSchema[]).some((b) => resolveField(b, segments));
  if (Array.isArray(schema.allOf))
    return (schema.allOf as JsonSchema[]).some((b) => resolveField(b, segments));
  const props = schema.properties as Record<string, JsonSchema> | undefined;
  if (props && Object.hasOwn(props, seg)) return resolveField(props[seg], rest);
  const extra = schema.additionalProperties;
  if (extra && typeof extra === 'object') return resolveField(extra as JsonSchema, rest);
  // A schema without type/properties/anyOf accepts anything (z.unknown / z.any).
  return Object.keys(schema).every(
    (k) => k === 'description' || k === 'title' || k === 'default',
  );
}

export function literalValues(field: JsonSchema | undefined): unknown[] | undefined {
  if (!field) return undefined;
  if (Array.isArray(field.enum)) return field.enum as unknown[];
  if (field.const !== undefined) return [field.const];
  if (field.type === 'boolean') return [true, false];
  const alts = (field.anyOf ?? field.oneOf) as JsonSchema[] | undefined;
  if (Array.isArray(alts)) {
    const all: unknown[] = [];
    for (const a of alts) {
      const l = literalValues(a);
      if (!l) return undefined;
      all.push(...l);
    }
    return all;
  }
  return undefined;
}
