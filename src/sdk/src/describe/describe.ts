// `describe`: evaluates the pipeline into the manifest and runs every describe-time check. All problems are collected and reported together.
import { z } from 'zod';
import { isModel } from '../core/models.ts';
import { pipelineData, type RetentionPolicy } from '../builder/pipeline.ts';
import { canonicalize, canonicalJson } from '../util/canonical.ts';
import type { HarnessInfo } from '../util/code-hash.ts';
import { API_VERSION, Manifest, type ManifestT } from './manifest-schema.ts';
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
}

export function describePipeline(pipeline: unknown, opts: DescribeOptions): ManifestT {
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
