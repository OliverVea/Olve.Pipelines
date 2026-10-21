/**
 * Form model for a pipeline input model's JSON Schema (the zod subset the SDK
 * emits: strings with pattern/length, enums, numbers/integers with bounds,
 * booleans, defaults, optional). Anything outside the subset becomes a JSON
 * field. The server re-validates; this only catches mistakes early.
 */
import type { JsonSchema } from './manifest.js';

export type FieldKind = 'string' | 'enum' | 'boolean' | 'integer' | 'number' | 'json';

export type FormField = {
  name: string;
  kind: FieldKind;
  required: boolean;
  hasDefault: boolean;
  default?: unknown;
  options?: unknown[];
  pattern?: RegExp;
  patternSource?: string;
  schema: JsonSchema;
  description?: string;
};

function typeOf(s: JsonSchema): string | undefined {
  if (Array.isArray(s.type)) return s.type.find((t) => t !== 'null');
  return s.type;
}

export function formFields(schema: JsonSchema | null): FormField[] {
  if (!schema || typeOf(schema) !== 'object' || !schema.properties) return [];
  const required = new Set(schema.required ?? []);
  return Object.entries(schema.properties).map(([name, s]) => {
    const t = typeOf(s);
    let kind: FieldKind = 'json';
    if (s.enum && s.enum.length) kind = 'enum';
    else if (s.const !== undefined) kind = 'enum';
    else if (t === 'string') kind = 'string';
    else if (t === 'boolean') kind = 'boolean';
    else if (t === 'integer') kind = 'integer';
    else if (t === 'number') kind = 'number';
    let pattern: RegExp | undefined;
    if (kind === 'string' && typeof s.pattern === 'string') {
      try {
        pattern = new RegExp(s.pattern, 'u');
      } catch {
        try {
          pattern = new RegExp(s.pattern);
        } catch {
          pattern = undefined; // unsupported syntax: leave it to the server
        }
      }
    }
    return {
      name,
      kind,
      required: required.has(name) && s.default === undefined,
      hasDefault: s.default !== undefined,
      default: s.default,
      options: s.enum ?? (s.const !== undefined ? [s.const] : undefined),
      pattern,
      patternSource: typeof s.pattern === 'string' ? s.pattern : undefined,
      schema: s,
      description: s.description,
    };
  });
}

/** Text representation of a value for the field's input control. */
export function toText(field: FormField, value: unknown): string {
  if (value === undefined || value === null) return '';
  if (field.kind === 'json') return JSON.stringify(value, null, 2);
  if (field.kind === 'enum') return JSON.stringify(value);
  return String(value);
}

export type Parsed = { ok: true; value: unknown; empty: boolean } | { ok: false; error: string };

/** Parse and validate one field's text against its schema. */
export function parseField(field: FormField, text: string): Parsed {
  const s = field.schema;
  if (text === '' && field.kind !== 'boolean') {
    if (field.required) return { ok: false, error: 'Required.' };
    return { ok: true, value: undefined, empty: true };
  }
  switch (field.kind) {
    case 'enum': {
      const v = JSON.parse(text) as unknown;
      return { ok: true, value: v, empty: false };
    }
    case 'boolean':
      if (text === '') {
        if (field.required) return { ok: false, error: 'Required.' };
        return { ok: true, value: undefined, empty: true };
      }
      return { ok: true, value: text === 'true', empty: false };
    case 'string': {
      if (s.minLength !== undefined && text.length < s.minLength)
        return { ok: false, error: `At least ${s.minLength} characters.` };
      if (s.maxLength !== undefined && text.length > s.maxLength)
        return { ok: false, error: `At most ${s.maxLength} characters.` };
      if (field.pattern && !field.pattern.test(text))
        return { ok: false, error: `Must match ${field.patternSource}.` };
      return { ok: true, value: text, empty: false };
    }
    case 'integer':
    case 'number': {
      const n = Number(text.trim());
      if (text.trim() === '' || Number.isNaN(n)) return { ok: false, error: 'Not a number.' };
      if (field.kind === 'integer' && !Number.isInteger(n)) return { ok: false, error: 'Must be a whole number.' };
      if (s.minimum !== undefined && n < s.minimum) return { ok: false, error: `At least ${s.minimum}.` };
      if (s.maximum !== undefined && n > s.maximum) return { ok: false, error: `At most ${s.maximum}.` };
      if (s.exclusiveMinimum !== undefined && n <= s.exclusiveMinimum)
        return { ok: false, error: `Greater than ${s.exclusiveMinimum}.` };
      if (s.exclusiveMaximum !== undefined && n >= s.exclusiveMaximum)
        return { ok: false, error: `Less than ${s.exclusiveMaximum}.` };
      return { ok: true, value: n, empty: false };
    }
    default:
      try {
        return { ok: true, value: JSON.parse(text), empty: false };
      } catch {
        return { ok: false, error: 'Not valid JSON.' };
      }
  }
}

/** Human-readable description of a field's constraints. */
export function constraintHint(field: FormField): string {
  const s = field.schema;
  const parts: string[] = [];
  if (field.kind === 'string' && field.patternSource) parts.push(`pattern ${field.patternSource}`);
  if (s.minLength !== undefined) parts.push(`min length ${s.minLength}`);
  if (s.maxLength !== undefined) parts.push(`max length ${s.maxLength}`);
  const big = 2 ** 53 - 1;
  if (s.minimum !== undefined && Math.abs(s.minimum) < big) parts.push(`≥ ${s.minimum}`);
  if (s.maximum !== undefined && Math.abs(s.maximum) < big) parts.push(`≤ ${s.maximum}`);
  if (s.exclusiveMinimum !== undefined) parts.push(`> ${s.exclusiveMinimum}`);
  if (s.exclusiveMaximum !== undefined) parts.push(`< ${s.exclusiveMaximum}`);
  if (field.hasDefault) parts.push(`default ${JSON.stringify(field.default)}`);
  return parts.join(', ');
}
