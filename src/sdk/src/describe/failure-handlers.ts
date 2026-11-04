import { isStep, type AnyStep } from '../core/steps.ts';
import type { FailureHandlerNode } from '../builder/pipeline.ts';
import type { ManifestT } from './manifest-schema.ts';
import type { DescribeContext } from './context.ts';
import { describeStep } from './step.ts';

/** Describes the `onFailure` handlers; call after the stages, so their scopes can name the stage steps. */
export function describeFailureHandlers(
  ctx: DescribeContext,
  handlers: readonly FailureHandlerNode[],
): ManifestT['failureHandlers'] {
  const stageStepNames = new Set(ctx.steps.keys());
  const seen = new Map<string, AnyStep>();
  return handlers
    .map((fh) => {
      if (!isStep(fh.step)) {
        ctx.problems.push('onFailure: handler is not a step');
        return undefined;
      }
      if (seen.get(fh.step.name) !== fh.step) ctx.registerStep(fh.step, 'onFailure');
      seen.set(fh.step.name, fh.step);
      const scope = fh.steps === null ? null : fh.steps.map((x) => x.name);
      for (const n of scope ?? [])
        if (!stageStepNames.has(n))
          ctx.problems.push(
            `onFailure(${fh.step.name}): '${n}' is not a step of this pipeline`,
          );
      return {
        step: describeStep(ctx, { step: fh.step }, new Map(), true),
        steps: scope,
      };
    })
    .filter((x) => x !== undefined);
}
