/**
 * Config documents in the browser: the version's config entries as one document schema, validation
 * with the same rules as the service (defaults filled before `required`, discriminated unions report
 * the branch the value picks, unknown top-level keys ignored), and schema lookups for completion.
 * The service validates again on save; this is for feedback while typing.
 */
import type { JsonSchema, Manifest } from './manifest.js';

export type ConfigEntry = {
  name: string;
  description: string;
  required: boolean;
  schema: JsonSchema;
};
export type Path = (string | number)[];
export type Issue = { path: Path; message: string };

export function configEntries(manifest: Manifest | null | undefined): ConfigEntry[] {
  return (manifest?.config ?? []) as ConfigEntry[];
}

/** One property per entry; required entries are required; other keys are not checked. */
export function documentSchema(entries: ConfigEntry[]): JsonSchema {
  return {
    type: 'object',
    properties: Object.fromEntries(
      entries.map((e) => [
        e.name,
        { ...e.schema, description: e.schema.description ?? e.description },
      ]),
    ),
    required: entries.filter((e) => e.required).map((e) => e.name),
  };
}

const isObject = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

function typeOf(v: unknown): string {
  if (v === null) return 'null';
  if (Array.isArray(v)) return 'array';
  if (typeof v === 'number') return Number.isInteger(v) ? 'integer' : 'number';
  return typeof v;
}

function matchesType(type: string | string[], v: unknown): boolean {
  const t = typeOf(v);
  return (Array.isArray(type) ? type : [type]).some(
    (x) => x === t || (x === 'number' && t === 'integer'),
  );
}

const clone = <T>(v: T): T =>
  v === undefined ? v : (JSON.parse(JSON.stringify(v)) as T);
const equal = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** Validates `value`; returns the issues and the value with defaults applied. */
export function validate(
  schema: JsonSchema,
  value: unknown,
  path: Path = [],
): { issues: Issue[]; value: unknown } {
  const issues: Issue[] = [];
  const out = check(schema, clone(value), path, issues);
  return { issues, value: out };
}

function check(s: JsonSchema, v: unknown, path: Path, issues: Issue[]): unknown {
  const union = (s.oneOf ?? s.anyOf) as JsonSchema[] | undefined;
  if (union) return checkUnion(union, Boolean(s.oneOf), v, path, issues);
  if (s.type !== undefined && !matchesType(s.type, v)) {
    issues.push({
      path,
      message: `expected ${Array.isArray(s.type) ? s.type.join(' or ') : s.type}, got ${typeOf(v)}`,
    });
    return v;
  }
  if (s.const !== undefined && !equal(s.const, v))
    issues.push({ path, message: `must be ${JSON.stringify(s.const)}` });
  if (s.enum && !s.enum.some((e) => equal(e, v)))
    issues.push({
      path,
      message: `must be one of ${s.enum.map((e) => JSON.stringify(e)).join(', ')}`,
    });
  if (isObject(v)) {
    const props = s.properties ?? {};
    for (const [k, p] of Object.entries(props))
      if (!(k in v) && p.default !== undefined) v[k] = clone(p.default);
    for (const r of s.required ?? [])
      if (!(r in v)) issues.push({ path: [...path, r], message: 'is required' });
    for (const k of Object.keys(v)) {
      if (props[k]) v[k] = check(props[k], v[k], [...path, k], issues);
      else if (s.additionalProperties === false)
        issues.push({ path: [...path, k], message: 'is not allowed (unknown property)' });
      else if (isObject(s.additionalProperties))
        v[k] = check(s.additionalProperties, v[k], [...path, k], issues);
    }
  } else if (Array.isArray(v) && s.items) {
    v.forEach((x, i) => (v[i] = check(s.items!, x, [...path, i], issues)));
  } else if (typeof v === 'string') {
    if (s.minLength !== undefined && v.length < s.minLength)
      issues.push({ path, message: `must be at least ${s.minLength} characters` });
    if (s.maxLength !== undefined && v.length > s.maxLength)
      issues.push({ path, message: `must be at most ${s.maxLength} characters` });
    if (s.pattern !== undefined && !safeTest(s.pattern, v))
      issues.push({ path, message: `must match ${s.pattern}` });
  } else if (typeof v === 'number') {
    if (s.minimum !== undefined && v < s.minimum)
      issues.push({ path, message: `must be ≥ ${s.minimum}` });
    if (s.maximum !== undefined && v > s.maximum)
      issues.push({ path, message: `must be ≤ ${s.maximum}` });
    if (s.exclusiveMinimum !== undefined && v <= s.exclusiveMinimum)
      issues.push({ path, message: `must be > ${s.exclusiveMinimum}` });
    if (s.exclusiveMaximum !== undefined && v >= s.exclusiveMaximum)
      issues.push({ path, message: `must be < ${s.exclusiveMaximum}` });
  }
  return v;
}

function safeTest(pattern: string, v: string): boolean {
  try {
    return new RegExp(pattern, 'u').test(v);
  } catch {
    return true;
  }
}

function checkUnion(
  branches: JsonSchema[],
  exactlyOne: boolean,
  v: unknown,
  path: Path,
  issues: Issue[],
): unknown {
  const attempts = branches.map((b) => {
    const branchIssues: Issue[] = [];
    const result = check(b, clone(v), path, branchIssues);
    return { b, result, issues: branchIssues };
  });
  const ok = attempts.filter((a) => a.issues.length === 0);
  if (ok.length === 1 || (ok.length > 1 && !exactlyOne)) return ok[0].result;
  if (ok.length > 1) {
    issues.push({ path, message: 'matches more than one of the allowed shapes' });
    return v;
  }
  const chosen = isObject(v) ? attempts.filter((a) => discriminatorsMatch(a.b, v)) : [];
  if (chosen.length === 1) issues.push(...chosen[0].issues);
  else
    issues.push({
      path,
      message: `must be one of: ${branches.map(describeBranch).join(' | ')}`,
    });
  return v;
}

function discriminatorsMatch(b: JsonSchema, v: Record<string, unknown>): boolean {
  const consts = Object.entries(b.properties ?? {}).filter(
    ([, p]) => p.const !== undefined,
  );
  return consts.length > 0 && consts.every(([k, p]) => equal(p.const, v[k]));
}

function describeBranch(b: JsonSchema): string {
  const consts = Object.entries(b.properties ?? {}).filter(
    ([, p]) => p.const !== undefined,
  );
  if (consts.length)
    return consts.map(([k, p]) => `${k}: ${JSON.stringify(p.const)}`).join(', ');
  if (b.const !== undefined) return JSON.stringify(b.const);
  return Array.isArray(b.type) ? b.type.join('/') : (b.type ?? 'value');
}

/** Every schema a value at `path` may follow (all union branches). */
export function schemasAt(schema: JsonSchema, path: Path): JsonSchema[] {
  let current: JsonSchema[] = [schema];
  for (const seg of path) {
    const next: JsonSchema[] = [];
    for (const s of current.flatMap(flatten)) {
      if (typeof seg === 'number' && s.items) next.push(s.items);
      else if (typeof seg === 'string' && s.properties?.[seg])
        next.push(s.properties[seg]);
      else if (typeof seg === 'string' && isObject(s.additionalProperties))
        next.push(s.additionalProperties as JsonSchema);
    }
    current = next;
  }
  return current.flatMap(flatten);
}

function flatten(s: JsonSchema): JsonSchema[] {
  const union = (s.oneOf ?? s.anyOf) as JsonSchema[] | undefined;
  return union ? union.flatMap(flatten) : [s];
}

/** Property names (with their schema) allowed at `path`. */
export function propertiesAt(schema: JsonSchema, path: Path): Map<string, JsonSchema> {
  const out = new Map<string, JsonSchema>();
  for (const s of schemasAt(schema, path))
    for (const [k, p] of Object.entries(s.properties ?? {}))
      if (!out.has(k)) out.set(k, p);
  return out;
}

/** Literal values allowed at `path` (enums, consts, booleans). */
export function valuesAt(schema: JsonSchema, path: Path): unknown[] {
  const out: unknown[] = [];
  for (const s of schemasAt(schema, path)) {
    if (s.enum) out.push(...s.enum);
    if (s.const !== undefined) out.push(s.const);
    if (s.type === 'boolean') out.push(true, false);
  }
  return [...new Map(out.map((v) => [JSON.stringify(v), v])).values()];
}
