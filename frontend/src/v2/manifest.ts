/**
 * Types and helpers for the v2 pipeline manifest (spec §4; grammar:
 * `sdk/manifest.schema.json`). The API serves a version's manifest as a JSON
 * string; these types are a hand-kept mirror of the schema's shape.
 */

export type JsonSchema = {
  type?: string | string[];
  properties?: Record<string, JsonSchema>;
  required?: string[];
  enum?: unknown[];
  const?: unknown;
  default?: unknown;
  pattern?: string;
  minimum?: number;
  maximum?: number;
  exclusiveMinimum?: number;
  exclusiveMaximum?: number;
  minLength?: number;
  maxLength?: number;
  format?: string;
  description?: string;
  items?: JsonSchema;
  anyOf?: JsonSchema[];
  additionalProperties?: boolean | JsonSchema;
  'x-olve-kind'?: string;
  [k: string]: unknown;
};

export type ModelSchemas = { input?: JsonSchema; output: JsonSchema };

export type Mapping = { ref: string } | { const: unknown } | { [field: string]: Mapping };

export type InputType = { model: string; optional: boolean } | { schema: ModelSchemas };

export type ManifestStep = {
  name: string;
  kind: string;
  image: string;
  input: Record<string, Mapping>;
  inputTypes: Record<string, InputType>;
  output: string | null;
  secrets: string[];
  timeoutSeconds?: number;
  handlerHash?: string;
  config?: unknown;
  [k: string]: unknown;
};

export type StepsStage = { steps: ManifestStep[] };
export type WhenStage = { when: Record<string, unknown[]>; stages: Stage[] };
export type Stage = StepsStage | WhenStage;

export type Trigger = {
  kind: 'push' | 'poll' | string;
  name?: string;
  url?: string;
  intervalSeconds?: number;
  valuePath?: string;
  secrets?: string[];
};

export type Manifest = {
  apiVersion: string;
  name: string;
  description?: string;
  input: string;
  harness?: { bun?: string; sdk?: string; sdkHash?: string };
  models: Record<string, ModelSchemas>;
  secrets: { name: string; description: string }[];
  triggers: Trigger[];
  stages: Stage[];
  failureHandlers: { step: ManifestStep; steps: string[] | null }[];
};

export function parseManifest(json: string | null | undefined): Manifest | null {
  if (!json) return null;
  try {
    return JSON.parse(json) as Manifest;
  } catch {
    return null;
  }
}

export function isWhen(stage: Stage): stage is WhenStage {
  return 'when' in stage && 'stages' in stage;
}

/** Every step in manifest order. */
export function allSteps(stages: Stage[]): ManifestStep[] {
  const out: ManifestStep[] = [];
  for (const s of stages) {
    if (isWhen(s)) out.push(...allSteps(s.stages));
    else out.push(...s.steps);
  }
  return out;
}

/** Steps of the manifest keyed by name. */
export function stepsByName(m: Manifest | null): Map<string, ManifestStep> {
  const map = new Map<string, ManifestStep>();
  if (!m) return map;
  for (const s of allSteps(m.stages)) map.set(s.name, s);
  return map;
}

/** The schema a trigger form/validation uses: the input view if it differs, else output. */
export function inputSchemaOf(m: Manifest, model: string): JsonSchema | null {
  const ms = m.models[model];
  return ms ? (ms.input ?? ms.output) : null;
}

export function outputSchemaOf(m: Manifest, model: string | null | undefined): JsonSchema | null {
  if (!model) return null;
  return m.models[model]?.output ?? null;
}

export type MappingLeaf = { path: string[]; kind: 'ref'; ref: string } | { path: string[]; kind: 'const'; value: unknown };

/**
 * Flatten a tagged mapping into leaves: `{ref}` and `{const}` are leaves (an
 * object whose only key is `ref` with a string value, or only key `const`);
 * anything else is a nested object built field by field.
 */
export function mappingLeaves(mapping: Mapping, path: string[] = []): MappingLeaf[] {
  if (mapping && typeof mapping === 'object') {
    const keys = Object.keys(mapping);
    if (keys.length === 1 && keys[0] === 'ref' && typeof (mapping as { ref: unknown }).ref === 'string')
      return [{ path, kind: 'ref', ref: (mapping as { ref: string }).ref }];
    if (keys.length === 1 && keys[0] === 'const')
      return [{ path, kind: 'const', value: (mapping as { const: unknown }).const }];
    return keys.flatMap((k) => mappingLeaves((mapping as Record<string, Mapping>)[k], [...path, k]));
  }
  return [];
}

/** All leaves of a step's input mapping, paths prefixed with the input entry name. */
export function stepWiring(step: ManifestStep): MappingLeaf[] {
  return Object.entries(step.input ?? {}).flatMap(([name, m]) => mappingLeaves(m, [name]));
}

/** Root of a ref (`input`, `failure` or a step name). */
export function refRoot(ref: string): string {
  const i = ref.indexOf('.');
  return i < 0 ? ref : ref.slice(0, i);
}

/** Step names a step reads from (its producers). */
export function producersOf(step: ManifestStep): Set<string> {
  const out = new Set<string>();
  for (const leaf of stepWiring(step)) {
    if (leaf.kind === 'ref') {
      const root = refRoot(leaf.ref);
      if (root !== 'input' && root !== 'failure') out.add(root);
    }
  }
  return out;
}

export function readsInput(step: ManifestStep): boolean {
  return stepWiring(step).some((l) => l.kind === 'ref' && refRoot(l.ref) === 'input');
}

/** A `when` block matches if every field's value is one of the listed values. */
export function whenMatches(when: Record<string, unknown[]>, input: Record<string, unknown>): boolean {
  return Object.entries(when).every(([field, values]) =>
    values.some((v) => JSON.stringify(v) === JSON.stringify(input[field])),
  );
}

export function formatWhen(when: Record<string, unknown[]>): string {
  return Object.entries(when)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([f, vs]) => (vs.length === 1 ? `${f} = ${fmt(vs[0])}` : `${f} ∈ {${vs.map(fmt).join(', ')}}`))
    .join(' and ');
}

function fmt(v: unknown): string {
  return typeof v === 'string' ? v : JSON.stringify(v);
}

/** Deterministic pretty JSON with sorted keys (for diffs). */
export function canonicalJson(value: unknown): string {
  return JSON.stringify(sortKeys(value), null, 2);
}

function sortKeys(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(sortKeys);
  if (v && typeof v === 'object') {
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(v as object).sort()) out[k] = sortKeys((v as Record<string, unknown>)[k]);
    return out;
  }
  return v;
}

/** Object schema of a step's input (entry name → model output view / plain schema). */
export function stepInputSchema(m: Manifest, step: ManifestStep): JsonSchema {
  const properties: Record<string, JsonSchema> = {};
  for (const [name, t] of Object.entries(step.inputTypes ?? {})) {
    properties[name] = 'model' in t ? (m.models[t.model]?.output ?? {}) : (t.schema.output ?? {});
  }
  return { type: 'object', properties };
}

/** Fill schema defaults into a (partial) input object, for plan previews. */
export function withDefaults(schema: JsonSchema | null, value: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = { ...value };
  for (const [k, s] of Object.entries(schema?.properties ?? {})) {
    if (out[k] === undefined && s.default !== undefined) out[k] = s.default;
  }
  return out;
}

export function inputTypeLabel(t: InputType | undefined): string {
  if (!t) return '';
  if ('model' in t) return t.optional ? `${t.model}?` : t.model;
  return 'schema';
}
