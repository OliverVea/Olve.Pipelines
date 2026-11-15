import { z } from 'zod';
import { model } from '../core/models.ts';
import type { Secret } from '../core/secrets.ts';
import type { Simplify } from '../core/types.ts';
import {
  Pipeline,
  pipelineData,
  isPipeline,
  type AvailMap,
  type CheckStage,
  type InitialAvail,
  type MakeMaybe,
  type OutputsOf,
  type PipelineData,
  type StageEntry,
  type WhenCond,
} from './pipeline.ts';

/**
 * What a CI run gives its steps: the repository, the commit tested (a pull request merged onto the base branch), the
 * run's kind and the pull request (0 for a queue run). A close run gets the commit its preview was deployed from.
 */
export const CiInput = model(
  'CiInput',
  z.object({
    repo: z.string(),
    sha: z.string(),
    kind: z.enum(['pull', 'queue', 'close']),
    pull: z.number().int(),
  }),
);

export interface ChecksDef {
  description?: string;
  /** The pipeline secrets CI steps may declare; a CI step declaring any other is a describe problem. */
  secrets?: readonly Secret[];
}

/** A pipeline's CI: stages over `CiInput`, built like the pipeline's. Export it as `ci` from the package's entry point. */
export interface Checks<A extends AvailMap> {
  /** Phantom: the availability map accumulated so far. */
  readonly __avail: A;
  /** Add a stage (its steps run in parallel); entries as in `pipeline().stage`. */
  stage<const E extends readonly StageEntry<A>[]>(
    ...entries: CheckStage<E, A>
  ): Checks<Simplify<A & OutputsOf<E, false>>>;
  /** Declarative condition on literal-like CI input fields. Outputs produced inside become maybe-available after. */
  when<B extends AvailMap>(
    cond: WhenCond<typeof CiInput>,
    block: (p: Checks<A>) => Checks<B>,
  ): Checks<Simplify<A & MakeMaybe<Omit<B, keyof A>>>>;
  /**
   * The one stage a close run runs once a pull request closes or merges, to remove its preview. It runs with the
   * definition of the run that deployed the preview, sees only `CiInput` (`kind: 'close'`), and its steps are never
   * reused. Steps of a pull request's run that deploy a preview declare `reuse: false`.
   */
  onClose<const E extends readonly StageEntry<InitialAvail<typeof CiInput>>[]>(
    ...entries: CheckStage<E, InitialAvail<typeof CiInput>>
  ): Checks<A>;
}

class ChecksPipeline extends Pipeline<typeof CiInput, AvailMap> {
  onClose(...entries: readonly unknown[]): unknown {
    const data = pipelineData(this);
    const stage = new Pipeline(
      { name: '', input: CiInput },
      { triggers: [], failureHandlers: [], problems: data.problems },
    );
    (stage.stage as (...e: readonly unknown[]) => unknown)(...entries);
    const node = pipelineData(stage).nodes[0];
    if (data.checks!.onClose)
      data.problems.push('onClose is declared twice; it is one stage');
    else if (node?.kind === 'stage') data.checks!.onClose = node.steps;
    return this;
  }
}

/** Start a pipeline's CI. Runs on pull requests into the binding's branch, apart from the pipeline's runs. */
export function checks(def: ChecksDef = {}): Checks<InitialAvail<typeof CiInput>> {
  const p = new ChecksPipeline({ name: '', input: CiInput });
  Object.assign(pipelineData(p), {
    checks: { description: def.description, secrets: [...(def.secrets ?? [])] },
  });
  return p as never;
}

export function isChecks(c: unknown): boolean {
  return isPipeline(c) && pipelineData(c).checks !== undefined;
}

/** The recorded CI of a `checks()` value. */
export function checksData(
  c: unknown,
): PipelineData & { checks: NonNullable<PipelineData['checks']> } {
  if (!isChecks(c))
    throw new TypeError('not checks (the `ci` export must be checks({...}).stage(...))');
  return pipelineData(c) as never;
}
