import { z } from 'zod';
import { isModel, isOptional } from '../core/models.ts';
import { isExternal } from '../core/external.ts';
import type { AnyStep } from '../core/steps.ts';

export type Validation<T> = { ok: true; value: T } | { ok: false; issues: string[] };

export function formatIssues(err: z.ZodError, prefix: string): string[] {
  return err.issues.map(
    (i) =>
      `${prefix}${i.path.length ? '.' + i.path.map(String).join('.') : ''}: ${i.message}`,
  );
}

const isPlainObject = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' &&
  v !== null &&
  !Array.isArray(v) &&
  (Object.getPrototypeOf(v) === Object.prototype || Object.getPrototypeOf(v) === null);

/**
 * zod strips unknown keys silently (at every depth). Compares the raw value with the parsed one:
 * any key present in a raw object but absent from the parsed object at the same place was stripped.
 */
export function strippedKeys(raw: unknown, parsed: unknown, path = ''): string[] {
  if (Array.isArray(raw) && Array.isArray(parsed))
    return raw.flatMap((x, i) => strippedKeys(x, parsed[i], `${path}[${i}]`));
  if (isPlainObject(raw) && isPlainObject(parsed)) {
    const out: string[] = [];
    for (const k of Object.keys(raw)) {
      const p = path ? `${path}.${k}` : k;
      if (!Object.hasOwn(parsed, k)) out.push(p);
      else out.push(...strippedKeys(raw[k], parsed[k], p));
    }
    return out;
  }
  return [];
}

/** Strict parse: zod validation plus rejection of unknown keys at any depth. */
export function parseStrict(
  schema: z.ZodType,
  value: unknown,
  prefix: string,
): Validation<unknown> {
  const r = schema.safeParse(value);
  if (!r.success) return { ok: false, issues: formatIssues(r.error, prefix) };
  const extra = strippedKeys(value, r.data);
  if (extra.length)
    return {
      ok: false,
      issues: extra.map(
        (k) => `${prefix}.${k}: unknown field (outputs are validated strictly)`,
      ),
    };
  return { ok: true, value: r.data };
}

function entrySchema(e: unknown): z.ZodType {
  if (isOptional(e) || isExternal(e)) return e.model.schema;
  if (isModel(e)) return e.schema;
  return e as z.ZodType;
}

/** Reserved input.json key holding the step's config entries (written by the service). */
export const CONFIG_KEY = '$config';

/** Parses `$config` with each declared entry's schema (defaults applied). Undeclared entries are ignored. */
export function validateConfig(
  step: AnyStep,
  raw: unknown,
): Validation<Record<string, unknown>> {
  const given = isPlainObject(raw) ? raw[CONFIG_KEY] : undefined;
  if (given !== undefined && !isPlainObject(given))
    return {
      ok: false,
      issues: [`input.${CONFIG_KEY} must be an object keyed by config entry names`],
    };
  const issues: string[] = [];
  const out: Record<string, unknown> = {};
  for (const e of step.config ?? []) {
    const value = given?.[e.name];
    const r = e.schema.safeParse(value);
    if (!r.success)
      issues.push(
        ...formatIssues(r.error, `config.${e.name}`).map((i) =>
          value === undefined ? `${i} (config entry '${e.name}' is not set)` : i,
        ),
      );
    else if (r.data !== undefined) out[e.name] = r.data;
  }
  return issues.length ? { ok: false, issues } : { ok: true, value: out };
}

/** Validates input.json against the step's input entries. Unknown top-level keys are rejected; nested values are parsed (unknown nested keys stripped). */
export function validateInput(
  step: AnyStep,
  raw: unknown,
): Validation<Record<string, unknown>> {
  if (!isPlainObject(raw))
    return {
      ok: false,
      issues: ["input.json must be a JSON object keyed by the step's input names"],
    };
  const issues: string[] = [];
  const out: Record<string, unknown> = {};
  const declared = step.input as Record<string, unknown>;
  for (const k of Object.keys(raw))
    if (k !== CONFIG_KEY && !Object.hasOwn(declared, k))
      issues.push(`input.${k}: not an input of step '${step.name}'`);
  for (const [k, e] of Object.entries(declared)) {
    if (!Object.hasOwn(raw, k)) {
      if (isOptional(e)) continue;
      issues.push(`input.${k}: missing`);
      continue;
    }
    const r = entrySchema(e).safeParse(raw[k]);
    if (r.success) out[k] = r.data;
    else issues.push(...formatIssues(r.error, `input.${k}`));
  }
  return issues.length ? { ok: false, issues } : { ok: true, value: out };
}

/** Validates a handler's return value strictly against the step's output model. */
export function validateOutput(step: AnyStep, value: unknown): Validation<unknown> {
  if (!step.output) {
    if (value === undefined || value === null) return { ok: true, value: null };
    return {
      ok: false,
      issues: [
        `step '${step.name}' declares no output model but the handler returned a value`,
      ],
    };
  }
  return parseStrict(step.output.schema, value, 'output');
}
