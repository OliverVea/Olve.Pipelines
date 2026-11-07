import { z } from 'zod';
import { CONFIG_NAME, isConfigEntry } from '../core/config.ts';
import { MODEL_NAME, type AnyModel } from '../core/models.ts';
import { SECRET_NAME, type Secret } from '../core/secrets.ts';
import { RESERVED_STEP_NAMES, STEP_NAME, type AnyStep } from '../core/steps.ts';
import { canonicalize } from '../util/canonical.ts';
import { secretRefsOf } from './config-schema.ts';
import type { ManifestT, ModelSchemasT } from './manifest-schema.ts';
import { schemaViews, type JsonSchema } from './schemas.ts';

/** Root name used in failure-handler mappings (the service supplies FailureContext under it). */
export const FAILURE_ROOT = 'failure';
const RESERVED = new Set([...RESERVED_STEP_NAMES, FAILURE_ROOT]);

/** What a step can read at a point of the pipeline: its output model, whether it may have been skipped, and whether it is a matrix step. */
export interface Avail {
  model: string | null;
  maybe: boolean;
  matrix?: boolean;
}

/** A select case a step sits in: which select (numbered in describe order), which value, and its condition text. */
export interface CaseOf {
  select: number;
  value: string;
  cond: string;
}

interface ConfigUse {
  description: string;
  required: boolean;
  schema: JsonSchema;
  key: string;
  secretRefs: string[];
}

// Two places are mutually exclusive when they sit in different cases of the same select.
const exclusive = (a: readonly CaseOf[], b: readonly CaseOf[]) =>
  a.some((x) => b.some((y) => y.select === x.select && y.value !== x.value));

const byName = ([a]: [string, unknown], [b]: [string, unknown]) =>
  a < b ? -1 : a > b ? 1 : 0;

/** The problems found so far and the models, secrets, config entries and step names the pipeline uses. */
export class DescribeContext {
  readonly problems: string[];
  readonly models = new Map<string, { schemas: ModelSchemasT; key: string }>();
  readonly secrets = new Map<string, string>();
  readonly configs = new Map<string, ConfigUse>();
  readonly steps = new Map<string, AnyStep>();
  private readonly placements = new Map<
    string,
    { step: AnyStep; cases: readonly CaseOf[] }[]
  >();
  private selects = 0;

  constructor(problems: readonly string[]) {
    this.problems = [...problems];
  }

  nextSelectId(): number {
    return ++this.selects;
  }

  useModel(m: AnyModel, at: string): string {
    const problems = this.problems;
    if (!MODEL_NAME.test(m.name))
      problems.push(`${at}: model name '${m.name}' must match ${MODEL_NAME}`);
    let schemas: ModelSchemasT;
    try {
      schemas = schemaViews(m.schema);
    } catch (e) {
      problems.push(
        `${at}: model '${m.name}' is not representable as JSON Schema (${(e as Error).message})`,
      );
      return m.name;
    }
    const key = JSON.stringify(schemas);
    const prev = this.models.get(m.name);
    if (!prev) this.models.set(m.name, { schemas, key });
    else if (prev.key !== key)
      problems.push(
        `two different schemas are registered under the model name '${m.name}' (one schema per model name)`,
      );
    return m.name;
  }

  outputViewOf(modelName: string | null): JsonSchema | undefined {
    return modelName ? this.models.get(modelName)?.schemas.output : undefined;
  }

  useSecret(s: Secret, at: string): string {
    if (!SECRET_NAME.test(s.name))
      this.problems.push(`${at}: secret name '${s.name}' must match ${SECRET_NAME}`);
    const prev = this.secrets.get(s.name);
    if (prev === undefined) this.secrets.set(s.name, s.description);
    else if (prev !== s.description)
      this.problems.push(
        `secret '${s.name}' is declared twice with different descriptions`,
      );
    return s.name;
  }

  useConfig(e: unknown, at: string): string | undefined {
    const problems = this.problems;
    if (!isConfigEntry(e))
      return void problems.push(
        `${at}: config entries must be config("name", "description", schema)`,
      );
    if (!CONFIG_NAME.test(e.name))
      problems.push(`${at}: config name '${e.name}' must match ${CONFIG_NAME}`);
    let schema: JsonSchema;
    try {
      schema = z.toJSONSchema(e.schema, {
        io: 'input',
        target: 'draft-2020-12',
      }) as JsonSchema;
      delete schema.$schema;
      schema = canonicalize(schema) as JsonSchema;
    } catch (err) {
      problems.push(
        `${at}: config '${e.name}' is not representable as JSON Schema (${(err as Error).message})`,
      );
      return e.name;
    }
    const key = JSON.stringify([e.description, schema]);
    const prev = this.configs.get(e.name);
    if (!prev)
      this.configs.set(e.name, {
        description: e.description,
        required: !e.schema.safeParse(undefined).success,
        schema,
        key,
        secretRefs: secretRefsOf(schema),
      });
    else if (prev.key !== key)
      problems.push(
        `two different config entries are named '${e.name}' (one description and schema per config name)`,
      );
    return e.name;
  }

  registerStep(s: AnyStep, where: string, cases: readonly CaseOf[] = []): void {
    const problems = this.problems;
    if (typeof s.name !== 'string' || !STEP_NAME.test(s.name))
      problems.push(
        `${where}: step name '${String(s.name)}' must be lower-case kebab (${STEP_NAME})`,
      );
    if (RESERVED.has(s.name))
      problems.push(`${where}: '${s.name}' is reserved and cannot be a step name`);
    const prev = this.placements.get(s.name) ?? [];
    if (prev.some((p) => !exclusive(p.cases, cases)))
      problems.push(`duplicate step name '${s.name}'`);
    else if (prev.some((p) => (p.step.output?.name ?? null) !== (s.output?.name ?? null)))
      problems.push(
        `step '${s.name}': its select cases must give it the same output model`,
      );
    else if (prev.some((p) => !p.step.matrix !== !s.matrix))
      problems.push(
        `step '${s.name}': its select cases must all make it a matrix step, or none`,
      );
    this.placements.set(s.name, [...prev, { step: s, cases }]);
    if (!this.steps.has(s.name)) this.steps.set(s.name, s);
  }

  modelsSection(): ManifestT['models'] {
    return Object.fromEntries([...this.models].map(([k, v]) => [k, v.schemas]));
  }

  secretsSection(): ManifestT['secrets'] {
    return [...this.secrets]
      .sort(byName)
      .map(([name, description]) => ({ name, description }));
  }

  configSection(): Pick<ManifestT, 'config'> {
    if (!this.configs.size) return {};
    return {
      config: [...this.configs].sort(byName).map(([name, c]) => ({
        name,
        description: c.description,
        required: c.required,
        schema: c.schema,
      })),
    };
  }
}
