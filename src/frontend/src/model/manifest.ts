/**
 * Types and helpers for the v2 pipeline manifest (spec §4; grammar:
 * `src/sdk/manifest.schema.json`). The API serves a version's manifest as a JSON
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
  oneOf?: JsonSchema[];
  additionalProperties?: boolean | JsonSchema;
  'x-olve-kind'?: string;
  [k: string]: unknown;
};

export type ModelSchemas = { input?: JsonSchema; output: JsonSchema };

export type Mapping = { ref: string } | { const: unknown } | { [field: string]: Mapping };

export type InputType = { model: string; optional: boolean } | { schema: ModelSchemas };

export type ExternalInput = {
  model: string;
  title: string;
  description: string;
  from: ExternalSource[];
};

export type ExternalSource =
  | { kind: 'person' }
  | { kind: 'callback' }
  | { kind: 'poll'; url: string; valuePath: string; intervalSeconds: number };

/** Whether a person may answer the input (the UI offers a form only then). */
export const byPerson = (x: ExternalInput) => x.from.some((f) => f.kind === 'person');

export type ManifestStep = {
  name: string;
  description?: string;
  kind: string;
  image: string;
  input: Record<string, Mapping>;
  inputTypes: Record<string, InputType>;
  output: string | null;
  secrets: string[];
  timeoutSeconds?: number;
  handlerHash?: string;
  config?: unknown;
  /** Config entries delivered to the step as ctx.config. */
  configEntries?: string[];
  /** Inputs answered from outside the run; the attempt waits until each has a value. */
  externalInputs?: Record<string, ExternalInput>;
  [k: string]: unknown;
};

export type StepsStage = { steps: ManifestStep[] };
export type WhenStage = { when: Record<string, unknown[]>; stages: Stage[] };
/** A branch on config: the case named by the config value at `select.discriminator` runs. */
export type SelectStage = {
  select: string;
  discriminator: string;
  cases: Record<string, Stage[]>;
};
export type Stage = StepsStage | WhenStage | SelectStage;

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
  /** Typed config entries (one key each in the pipeline's config document). */
  config?: { name: string; description: string; required: boolean; schema: JsonSchema }[];
  /** Per-pipeline retention overrides (each replaces the instance setting of the same meaning). */
  retention?: {
    keepLatestRuns?: number;
    minRunAgeDays?: number;
    buildGraceDays?: number;
  };
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

export function isSelect(stage: Stage): stage is SelectStage {
  return 'select' in stage && 'cases' in stage;
}

/** Every step in manifest order (every case of a select). */
export function allSteps(stages: Stage[]): ManifestStep[] {
  const out: ManifestStep[] = [];
  for (const s of stages) {
    if (isWhen(s)) out.push(...allSteps(s.stages));
    else if (isSelect(s))
      for (const c of Object.values(s.cases)) out.push(...allSteps(c));
    else out.push(...s.steps);
  }
  return out;
}

/** "target.prod.kind = ssh" */
export function formatCase(stage: SelectStage, value: string): string {
  return `${stage.select}.${stage.discriminator}: ${value}`;
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

export function outputSchemaOf(
  m: Manifest,
  model: string | null | undefined,
): JsonSchema | null {
  if (!model) return null;
  return m.models[model]?.output ?? null;
}

function refRoots(mapping: Mapping): string[] {
  if (!mapping || typeof mapping !== 'object') return [];
  if ('ref' in mapping && typeof mapping.ref === 'string')
    return [mapping.ref.split('.')[0]];
  if ('const' in mapping) return [];
  return Object.values(mapping as Record<string, Mapping>).flatMap(refRoots);
}

/** Names of the steps a step reads from. */
export function producersOf(step: ManifestStep): Set<string> {
  const roots = Object.values(step.input ?? {}).flatMap(refRoots);
  return new Set(roots.filter((r) => r !== 'input' && r !== 'failure'));
}

export function formatWhen(when: Record<string, unknown[]>): string {
  return Object.entries(when)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([f, vs]) =>
      vs.length === 1 ? `${f}: ${fmt(vs[0])}` : `${f}: ${vs.map(fmt).join(' | ')}`,
    )
    .join(', ');
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
    for (const k of Object.keys(v as object).sort())
      out[k] = sortKeys((v as Record<string, unknown>)[k]);
    return out;
  }
  return v;
}

/** Object schema of a step's input (entry name → model output view / plain schema). */
export function stepInputSchema(m: Manifest, step: ManifestStep): JsonSchema {
  const properties: Record<string, JsonSchema> = {};
  for (const [name, t] of Object.entries(step.inputTypes ?? {})) {
    properties[name] =
      'model' in t ? (m.models[t.model]?.output ?? {}) : (t.schema.output ?? {});
  }
  for (const [name, x] of Object.entries(step.externalInputs ?? {}))
    properties[name] = m.models[x.model]?.output ?? {};
  return { type: 'object', properties };
}
