/**
 * The config form's model: what kind of field a schema is, which branch of a union a value
 * follows, what a new value starts as, and edits that keep the YAML document's comments.
 */
import { isMap, isScalar, isSeq, parseDocument, type Document } from 'yaml';
import type { JsonSchema } from './manifest.js';
import type { Path } from './config-schema.js';

export type FieldKind =
  | 'object'
  | 'union'
  | 'secret'
  | 'enum'
  | 'string'
  | 'number'
  | 'integer'
  | 'boolean'
  | 'list'
  | 'record'
  | 'json';

const isObject = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const typeOf = (s: JsonSchema): string | undefined =>
  Array.isArray(s.type) ? s.type.find((t) => t !== 'null') : s.type;

const branches = (s: JsonSchema): JsonSchema[] | undefined => s.oneOf ?? s.anyOf;

export function kindOf(s: JsonSchema): FieldKind {
  if (s['x-olve-kind'] === 'secretRef') return 'secret';
  if (branches(s)) return unionOf(s) ? 'union' : 'json';
  if (s.enum?.length || s.const !== undefined) return 'enum';
  const t = typeOf(s);
  if (t === 'object')
    return s.properties ? 'object' : isObject(s.additionalProperties) ? 'record' : 'json';
  if (t === 'array') return s.items ? 'list' : 'json';
  if (t === 'string' || t === 'number' || t === 'integer' || t === 'boolean') return t;
  return 'json';
}

/** The values a field may take, for a dropdown. */
export const optionsOf = (s: JsonSchema): unknown[] =>
  s.enum ?? (s.const !== undefined ? [s.const] : []);

/** The secret a `{ secret: NAME }` field names. */
export function secretNameOf(s: JsonSchema): string | undefined {
  const name = s.properties?.secret?.const;
  return typeof name === 'string' ? name : undefined;
}

export type Union = {
  /** The property every branch fixes with a `const`, e.g. `kind`. */
  discriminator: string;
  options: { value: unknown; branch: JsonSchema }[];
};

/** A union whose object branches each fix the same property to a different constant. */
export function unionOf(s: JsonSchema): Union | undefined {
  const list = branches(s);
  if (!list?.length || !list.every((b) => b.properties)) return undefined;
  const candidates = Object.keys(list[0].properties!).filter((k) =>
    list.every((b) => b.properties?.[k]?.const !== undefined),
  );
  const discriminator = candidates[0];
  if (discriminator === undefined) return undefined;
  return {
    discriminator,
    options: list.map((branch) => ({
      value: branch.properties![discriminator].const,
      branch,
    })),
  };
}

/** The branch `value` follows: by its discriminator, else the first. */
export function branchOf(u: Union, value: unknown): JsonSchema {
  const picked = isObject(value) ? value[u.discriminator] : undefined;
  return (u.options.find((o) => o.value === picked) ?? u.options[0]).branch;
}

/** What a field starts as when it is added: its default, else the smallest value its schema allows. */
export function seedOf(s: JsonSchema): unknown {
  if (s.default !== undefined) return structuredClone(s.default);
  switch (kindOf(s)) {
    case 'union': {
      const u = unionOf(s)!;
      return seedOf(u.options[0].branch);
    }
    case 'secret':
      return { secret: secretNameOf(s) };
    case 'enum':
      return optionsOf(s)[0];
    case 'object': {
      const out: Record<string, unknown> = {};
      const required = new Set(s.required ?? []);
      for (const [k, p] of Object.entries(s.properties ?? {}))
        if (p.const !== undefined || (required.has(k) && p.default === undefined))
          out[k] = seedOf(p);
      return out;
    }
    case 'boolean':
      return false;
    case 'number':
    case 'integer':
      return s.minimum ?? 0;
    case 'list':
      return [];
    case 'record':
      return {};
    case 'string':
      return '';
    default:
      return null;
  }
}

/** A new value for a union field switched to the branch with `value`, keeping fields both share. */
export function switchBranch(u: Union, current: unknown, value: unknown): unknown {
  const option = u.options.find((o) => o.value === value) ?? u.options[0];
  const next = seedOf(option.branch) as Record<string, unknown>;
  if (isObject(current))
    for (const [k, v] of Object.entries(current))
      if (k !== u.discriminator && option.branch.properties?.[k]) next[k] = v;
  return next;
}

export function valueAt(value: unknown, path: Path): unknown {
  let v = value;
  for (const seg of path) {
    if (Array.isArray(v) && typeof seg === 'number') v = v[seg];
    else if (isObject(v) && typeof seg === 'string') v = v[seg];
    else return undefined;
  }
  return v;
}

/** The YAML text with the value at `path` set (`undefined` removes it); comments elsewhere survive. */
export function editYaml(text: string, path: Path, value: unknown): string {
  const doc: Document = parseDocument(text || '{}');
  if (!isMap(doc.contents) && !isSeq(doc.contents)) doc.contents = doc.createNode({});
  const node = doc.getIn(path, true);
  if (value === undefined) doc.deleteIn(path);
  // A scalar changed in place keeps its comment.
  else if (isScalar(node) && (value === null || typeof value !== 'object'))
    node.value = value;
  else doc.setIn(path, doc.createNode(value));
  return doc.toString();
}

/** The document's value, or `{}` when it is empty or not a mapping. */
export function yamlValue(text: string): Record<string, unknown> {
  const v: unknown = parseDocument(text).toJS();
  return isObject(v) ? v : {};
}
