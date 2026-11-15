// `describe`: evaluates the pipeline into the manifest and runs every describe-time check. All problems are collected and reported together.
import { z } from 'zod';
import { isModel } from '../core/models.ts';
import {
  pipelineData,
  type PipelineData,
  type RetentionPolicy,
  type StageNode,
} from '../builder/pipeline.ts';
import { CiInput, isChecks } from '../builder/checks.ts';
import { isImplementation } from '../core/handlers.ts';
import type { AnyStep } from '../core/steps.ts';
import { canonicalize, canonicalJson } from '../util/canonical.ts';
import type { HarnessInfo } from '../util/code-hash.ts';
import {
  API_VERSION,
  Manifest,
  type ManifestT,
  type Stage,
  type StepDescriptorT,
} from './manifest-schema.ts';
import { DescribeContext } from './context.ts';
import { describeStages } from './stages.ts';
import { describeFailureHandlers } from './failure-handlers.ts';
import { describeTriggers } from './triggers.ts';

export { FAILURE_ROOT } from './context.ts';
export { schemaViews } from './schemas.ts';
export { recordedConfigRefs, secretRefsOf, subSchemas } from './config-schema.ts';

export class DescribeError extends Error {
  override name = 'DescribeError';
  constructor(readonly problems: readonly string[]) {
    super(
      `describe found ${problems.length} problem(s):\n${problems.map((p) => `  - ${p}`).join('\n')}`,
    );
  }
}

export interface DescribeOptions {
  /** Hash of the build (from the build; includes the harness identity). */
  codeHash?: string;
  harness: HarnessInfo;
  /** The entry point's `ci` export, if it has one. */
  ci?: unknown;
}

export function describePipeline(pipeline: unknown, opts: DescribeOptions): ManifestT {
  if (isChecks(pipeline))
    throw new DescribeError([
      'the default export is checks({...}); default-export the pipeline and export its CI as `ci`',
    ]);
  const data = pipelineData(pipeline);
  const ctx = new DescribeContext(data.problems);

  const inputModel = data.input;
  if (!isModel(inputModel))
    throw new DescribeError([
      'pipeline input must be a model (model("Name", z.object({...})))',
    ]);
  ctx.useModel(inputModel, 'pipeline input');

  const stages = describeStages(
    ctx,
    inputModel,
    data.nodes,
    new Map([['input', { model: inputModel.name, maybe: false }]]),
  );
  const failureHandlers = describeFailureHandlers(ctx, data.failureHandlers);
  const triggers = describeTriggers(ctx, data.triggers);
  const retention = describeRetention(ctx, data.retention);
  const ci =
    opts.ci === undefined
      ? undefined
      : describeCi(ctx, opts.ci, stages, failureHandlers, data);

  if (ctx.problems.length) throw new DescribeError(ctx.problems);

  const manifest = canonicalize({
    apiVersion: API_VERSION,
    name: data.name,
    description: data.description,
    input: inputModel.name,
    ...(Object.keys(retention).length ? { retention } : {}),
    harness: opts.harness,
    codeHash: opts.codeHash ?? 'sha256:' + '0'.repeat(64),
    models: ctx.modelsSection(),
    secrets: ctx.secretsSection(),
    ...ctx.configSection(),
    triggers,
    stages,
    ...(ci ? { ci } : {}),
    failureHandlers,
  });
  const checked = Manifest.safeParse(manifest);
  if (!checked.success)
    throw new DescribeError([
      `internal: manifest does not match its schema: ${z.prettifyError(checked.error)}`,
    ]);
  return manifest as ManifestT;
}

function describeRetention(
  ctx: DescribeContext,
  policy: RetentionPolicy | undefined,
): Record<string, number> {
  const retention: Record<string, number> = {};
  for (const key of ['keepLatestRuns', 'minRunAgeDays', 'buildGraceDays'] as const) {
    const v = policy?.[key];
    if (v === undefined) continue;
    if (!(Number.isInteger(v) && v >= 0))
      ctx.problems.push(`retention.${key} must be a non-negative integer`);
    else retention[key] = v;
  }
  return retention;
}

/** Canonical manifest text (what `describe` prints and the service hashes). */
export const manifestText = (m: ManifestT): string => canonicalJson(m);

function describeCi(
  ctx: DescribeContext,
  ci: unknown,
  pipelineStages: readonly Stage[],
  failureHandlers: ManifestT['failureHandlers'],
  pipeline: PipelineData,
): ManifestT['ci'] {
  if (!isChecks(ci)) {
    ctx.problems.push('the `ci` export must be checks({...}).stage(...)');
    return undefined;
  }
  const data = pipelineData(ci);
  const ciCtx = ctx.scope(data.problems);
  const stages = describeStages(
    ciCtx,
    CiInput,
    data.nodes,
    new Map([['input', { model: ciCtx.useModel(CiInput, 'CI input'), maybe: false }]]),
  );
  const allowed = new Set(data.checks!.secrets.map((s) => s.name));
  const pipelineSteps = new Map<string, StepDescriptorT>();
  for (const d of descriptors(pipelineStages))
    if (!pipelineSteps.has(d.name)) pipelineSteps.set(d.name, d);
  for (const fh of failureHandlers)
    if (!pipelineSteps.has(fh.step.name)) pipelineSteps.set(fh.step.name, fh.step);
  const pipelineObjects = stepObjects(pipeline);
  for (const d of descriptors(stages)) {
    for (const secret of d.secrets ?? [])
      if (!allowed.has(secret))
        ctx.problems.push(
          `CI step '${d.name}' uses secret '${secret}', which is not in ci.secrets`,
        );
    if (d.externalInputs)
      ctx.problems.push(
        `CI step '${d.name}' waits for external inputs; nobody answers a pull request's step`,
      );
    const other = pipelineSteps.get(d.name);
    if (other && contract(other) !== contract(d))
      ctx.problems.push(
        `CI step '${d.name}' differs from the pipeline's step '${d.name}'; a step in both must be the same step`,
      );
  }
  for (const [name, step] of ciCtx.steps) {
    const other = pipelineObjects.get(name);
    if (other && other !== step && handlerText(other) !== handlerText(step))
      ctx.problems.push(
        `CI step '${name}' has a different implementation than the pipeline's step '${name}'; a step in both must be the same step`,
      );
  }
  for (const name of allowed)
    if (!ctx.secrets.has(name))
      ctx.problems.push(
        `ci.secrets lists '${name}', which is not in the pipeline's secrets`,
      );
  return {
    ...(data.checks!.description !== undefined
      ? { description: data.checks!.description }
      : {}),
    input: CiInput.name,
    secrets: [...allowed].sort(),
    stages,
  };
}

function descriptors(stages: readonly Stage[]): StepDescriptorT[] {
  return stages.flatMap((st) =>
    'steps' in st
      ? st.steps
      : 'when' in st
        ? descriptors(st.stages)
        : Object.values(st.cases).flatMap(descriptors),
  );
}

function contract(d: StepDescriptorT): string {
  const { description: _d, input: _i, ...rest } = d;
  return canonicalJson(rest);
}

function stepObjects(data: PipelineData): Map<string, AnyStep> {
  const out = new Map<string, AnyStep>();
  const walk = (nodes: readonly StageNode[]) => {
    for (const n of nodes)
      if (n.kind === 'stage') for (const s of n.steps) out.set(s.step.name, s.step);
      else if (n.kind === 'when') walk(n.nodes);
      else for (const inner of Object.values(n.cases)) walk(inner);
  };
  walk(data.nodes);
  for (const fh of data.failureHandlers)
    if (!out.has(fh.step.name)) out.set(fh.step.name, fh.step);
  return out;
}

const handlerText = (s: AnyStep): string =>
  isImplementation(s) ? String(s.handler) : '';
