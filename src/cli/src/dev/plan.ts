import { CliError } from '../errors';
import {
  conditionText,
  findStep,
  type DevManifest,
  type DevStep,
  type Json,
  type JsonObject,
  type StageNode,
  type WhenCondition,
} from './manifest';

export type PlannedStep = {
  step: DevStep;
  stage: number;
  conditions: WhenCondition[];
  cases: string[];
};

/** Every step in run order, numbered by stage, with the `when` conditions around it and the select cases it is in. */
export function flatten(manifest: DevManifest, config?: JsonObject): PlannedStep[] {
  const result: PlannedStep[] = [];
  let stage = 0;
  const walk = (nodes: StageNode[], conditions: WhenCondition[], cases: string[]) => {
    for (const node of nodes) {
      if (node.kind === 'stage') {
        stage++;
        for (const step of node.steps) result.push({ step, stage, conditions, cases });
      } else if (node.kind === 'when')
        walk(node.nodes, [...conditions, ...node.conditions], cases);
      else {
        // With a config only its case runs; without one, every case does.
        const value = selectedValue(node.select, node.discriminator, config);
        for (const [key, caseNodes] of Object.entries(node.cases))
          if (value === undefined || key === value)
            walk(caseNodes, conditions, [
              ...cases,
              `${node.select}.${node.discriminator}=${key}`,
            ]);
      }
    }
  };
  walk(manifest.stages, [], []);
  return result;
}

function selectedValue(
  select: string,
  discriminator: string,
  config?: JsonObject,
): string | undefined {
  let cur: Json | undefined = config;
  for (const seg of [...select.split('.'), discriminator])
    cur = cur && typeof cur === 'object' && !Array.isArray(cur) ? cur[seg] : undefined;
  return typeof cur === 'string' ? cur : undefined;
}

const sameJson = (a: Json | undefined, b: Json | undefined) =>
  JSON.stringify(a) === JSON.stringify(b);

/** Why the conditions don't match the input, or undefined when they all do. */
export function whenMismatch(
  conditions: WhenCondition[],
  input: JsonObject | undefined,
): string | undefined {
  for (const c of conditions) {
    const present = !!input && c.field in input;
    const actual = present ? input![c.field] : undefined;
    if (present && c.values.some((v) => sameJson(v, actual))) continue;
    return `when ${conditionText(c)} did not match (${c.field} = ${present ? JSON.stringify(actual) : 'unset'})`;
  }
  return undefined;
}

/** `--until` keeps the stages through that step's; `--only` keeps just the named steps. */
export function select(
  plan: PlannedStep[],
  until?: string,
  only?: string[],
): PlannedStep[] {
  const known = plan.map((p) => p.step.name);
  const problems: string[] = [];
  if (until !== undefined && !known.includes(until))
    problems.push(`--until: unknown step '${until}' (steps: ${known.join(', ')}).`);
  for (const name of only ?? [])
    if (!known.includes(name))
      problems.push(`--only: unknown step '${name}' (steps: ${known.join(', ')}).`);
  if (problems.length) throw new CliError(problems);
  let selected = plan;
  if (until !== undefined) {
    const last = plan.find((p) => p.step.name === until)!.stage;
    selected = selected.filter((p) => p.stage <= last);
  }
  if (only?.length) selected = selected.filter((p) => only.includes(p.step.name));
  return selected;
}

/** Where `stepName` runs under this config; a failure handler has no stage. */
export function placeStep(
  manifest: DevManifest,
  stepName: string,
  config?: JsonObject,
): PlannedStep {
  const placements = flatten(manifest, config).filter((p) => p.step.name === stepName);
  if (placements.length === 1) return placements[0]!;
  if (placements.length > 1)
    throw new CliError(
      `'${stepName}' is in several select cases (${[...new Set(placements.flatMap((p) => p.cases))].join(', ')}); pass --config or --config-from to pick one.`,
    );
  const step = findStep(manifest, stepName);
  if (step?.isFailureHandler) return { step, stage: 0, conditions: [], cases: [] };
  if (step)
    throw new CliError(`'${stepName}' is not in the select case this config picks.`);
  throw new CliError(
    `Unknown step '${stepName}' (steps: ${[...new Set(flatten(manifest).map((p) => p.step.name)), ...manifest.failureHandlers.map((h) => h.step.name)].join(', ')}).`,
  );
}
