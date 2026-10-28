import { CliError, describe } from '../errors';

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
export type JsonObject = { [key: string]: Json };

export type DevStep = {
  name: string;
  kind: string;
  image: string;
  output: string | null;
  secrets: string[];
  /** The tagged mappings (`{ref}`, `{const}`, nested objects) the input is built from. */
  input: JsonObject;
  optionalInputs: Record<string, boolean>;
  isFailureHandler: boolean;
  /** External input name -> its model. */
  externalInputs: Record<string, string>;
};

export type WhenCondition = { field: string; values: Json[] };

export type StageNode =
  | { kind: 'stage'; steps: DevStep[] }
  | { kind: 'when'; conditions: WhenCondition[]; nodes: StageNode[] }
  | {
      kind: 'select';
      select: string;
      discriminator: string;
      cases: Record<string, StageNode[]>;
    };

export type DevManifest = {
  text: string;
  name: string;
  description?: string;
  inputModel?: string;
  /** The pipeline input's `z.input` view (defaults optional). */
  inputSchema?: JsonObject;
  /** Its strict `z.output` view (what the pool holds). */
  inputOutputSchema?: JsonObject;
  secrets: { name: string; description: string }[];
  triggers: JsonObject[];
  stages: StageNode[];
  failureHandlers: { step: DevStep; scope?: string[] }[];
};

export const str = (v: Json | undefined): string | undefined =>
  typeof v === 'string' ? v : undefined;
const obj = (v: Json | undefined): JsonObject | undefined =>
  v && typeof v === 'object' && !Array.isArray(v) ? v : undefined;
const arr = (v: Json | undefined): Json[] => (Array.isArray(v) ? v : []);

export const conditionText = (c: WhenCondition) =>
  `${c.field} in [${c.values.map((v) => JSON.stringify(v)).join(', ')}]`;

export function parseManifest(text: string): DevManifest {
  let root: Json;
  try {
    root = JSON.parse(text) as Json;
  } catch (error) {
    throw new CliError(
      `The manifest printed by describe is not JSON: ${describe(error)}`,
    );
  }
  const m = obj(root);
  if (!m) throw new CliError('The manifest printed by describe is not a JSON object.');
  const inputModel = str(m.input);
  const model = inputModel ? obj(obj(m.models)?.[inputModel]) : undefined;
  const outputSchema = obj(model?.output);
  return {
    text,
    name: str(m.name) ?? 'pipeline',
    description: str(m.description),
    inputModel,
    inputSchema: obj(model?.input) ?? outputSchema,
    inputOutputSchema: outputSchema,
    secrets: arr(m.secrets).flatMap((s) =>
      obj(s)
        ? [{ name: str(obj(s)!.name) ?? '', description: str(obj(s)!.description) ?? '' }]
        : [],
    ),
    triggers: arr(m.triggers).flatMap((t) => (obj(t) ? [obj(t)!] : [])),
    stages: parseNodes(arr(m.stages)),
    failureHandlers: arr(m.failureHandlers).flatMap((h) => {
      const handler = obj(h);
      const step = obj(handler?.step);
      if (!handler || !step) return [];
      const scope = Array.isArray(handler.steps)
        ? handler.steps.flatMap((s) => (typeof s === 'string' ? [s] : []))
        : undefined;
      return [{ step: parseStep(step, true), scope }];
    }),
  };
}

function parseNodes(nodes: Json[]): StageNode[] {
  const result: StageNode[] = [];
  for (const raw of nodes) {
    const node = obj(raw);
    if (!node) continue;
    if (Array.isArray(node.steps))
      result.push({
        kind: 'stage',
        steps: node.steps.flatMap((s) => (obj(s) ? [parseStep(obj(s)!, false)] : [])),
      });
    else if (obj(node.when))
      result.push({
        kind: 'when',
        conditions: Object.entries(obj(node.when)!).map(([field, v]) => ({
          field,
          values: Array.isArray(v) ? v : [v],
        })),
        nodes: parseNodes(arr(node.stages)),
      });
    else if (typeof node.select === 'string' && obj(node.cases))
      result.push({
        kind: 'select',
        select: node.select,
        discriminator: str(node.discriminator) ?? '',
        cases: Object.fromEntries(
          Object.entries(obj(node.cases)!).map(([k, v]) => [k, parseNodes(arr(v))]),
        ),
      });
  }
  return result;
}

function parseStep(step: JsonObject, isFailureHandler: boolean): DevStep {
  const optionalInputs: Record<string, boolean> = {};
  for (const [key, type] of Object.entries(obj(step.inputTypes) ?? {}))
    optionalInputs[key] = obj(type)?.optional === true;
  return {
    name: str(step.name) ?? '',
    kind: str(step.kind) ?? 'custom',
    image: str(step.image) ?? '',
    output: str(step.output) ?? null,
    secrets: arr(step.secrets).flatMap((s) => (typeof s === 'string' ? [s] : [])),
    input: structuredClone(obj(step.input) ?? {}),
    optionalInputs,
    isFailureHandler,
    externalInputs: Object.fromEntries(
      Object.entries(obj(step.externalInputs) ?? {}).map(([k, v]) => [
        k,
        str(obj(v)?.model) ?? '?',
      ]),
    ),
  };
}

export function* stageSteps(nodes: StageNode[]): Generator<DevStep> {
  for (const node of nodes) {
    if (node.kind === 'stage') yield* node.steps;
    else if (node.kind === 'when') yield* stageSteps(node.nodes);
    else for (const c of Object.values(node.cases)) yield* stageSteps(c);
  }
}

export function allSteps(m: DevManifest): DevStep[] {
  return [...stageSteps(m.stages), ...m.failureHandlers.map((h) => h.step)];
}

export const findStep = (m: DevManifest, name: string) =>
  allSteps(m).find((s) => s.name === name);
