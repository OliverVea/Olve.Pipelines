import { configRefPath, configRefTarget, isConfigRef, unionAt } from '../core/config.ts';
import { isStep } from '../core/steps.ts';
import type { StageNode } from '../builder/pipeline.ts';
import type { Stage, StepDescriptorT } from './manifest-schema.ts';
import { literalValues, type JsonSchema } from './schemas.ts';
import type { Avail, CaseOf, DescribeContext } from './context.ts';
import { describeStep } from './step.ts';

type StageGroup = Extract<StageNode, { kind: 'stage' }>;
type WhenNode = Extract<StageNode, { kind: 'when' }>;
type SelectNode = Extract<StageNode, { kind: 'select' }>;

/** Describes stages, `when` blocks and selects in order, adding what each makes available to `avail`. */
export function describeStages(
  ctx: DescribeContext,
  input: { name: string },
  nodes: readonly StageNode[],
  avail: Map<string, Avail>,
  cases: readonly CaseOf[] = [],
): Stage[] {
  const out: Stage[] = [];
  for (const n of nodes) {
    if (n.kind === 'select') {
      out.push(describeSelect(ctx, input, n, avail, cases, ctx.nextSelectId()));
      continue;
    }
    if (n.kind === 'stage') {
      const stage = describeStage(ctx, n, avail, cases);
      if (stage) out.push(stage);
    } else out.push(describeWhen(ctx, input, n, avail, cases));
  }
  return out;
}

function describeStage(
  ctx: DescribeContext,
  n: StageGroup,
  avail: Map<string, Avail>,
  cases: readonly CaseOf[],
): Stage | undefined {
  const descs = n.steps
    .map((ss) => {
      if (!isStep(ss.step)) {
        ctx.problems.push('a stage entry is not a step');
        return undefined;
      }
      ctx.registerStep(ss.step, 'stage', cases);
      return describeStep(ctx, ss, avail, false, cases);
    })
    .filter((d): d is StepDescriptorT => d !== undefined);
  // Outputs become available only after the barrier (same-stage siblings can't see each other).
  for (const ss of n.steps)
    if (isStep(ss.step))
      avail.set(ss.step.name, {
        model: ss.step.output?.name ?? null,
        maybe: false,
      });
  return descs.length ? { steps: descs } : undefined;
}

function describeWhen(
  ctx: DescribeContext,
  input: { name: string },
  n: WhenNode,
  avail: Map<string, Avail>,
  cases: readonly CaseOf[],
): Stage {
  const problems = ctx.problems;
  const when: Record<string, (string | number | boolean)[]> = {};
  const props = (ctx.models.get(input.name)?.schemas.output.properties ?? {}) as Record<
    string,
    JsonSchema
  >;
  for (const [field, raw] of Object.entries(n.cond)) {
    const values = (Array.isArray(raw) ? raw : [raw]) as (string | number | boolean)[];
    if (!Object.hasOwn(props, field))
      problems.push(
        `when: '${field}' is not a field of the pipeline input ${input.name}`,
      );
    else {
      const literals = literalValues(props[field]);
      if (!literals)
        problems.push(
          `when: '${field}' is not a literal-like field (enum, boolean or literal)`,
        );
      else
        for (const v of values)
          if (!literals.includes(v))
            problems.push(
              `when: ${JSON.stringify(v)} is not a valid value of '${field}' (${literals.map((l) => JSON.stringify(l)).join(', ')})`,
            );
    }
    if (values.length === 0) problems.push(`when: '${field}' has no values`);
    when[field] = values;
  }
  const inner = new Map(avail);
  const stages = describeStages(ctx, input, n.nodes, inner, cases);
  for (const [name, a] of inner)
    if (!avail.has(name)) avail.set(name, { model: a.model, maybe: true });
  return { when, stages };
}

function describeSelect(
  ctx: DescribeContext,
  input: { name: string },
  n: SelectNode,
  avail: Map<string, Avail>,
  cases: readonly CaseOf[],
  id: number,
): Stage {
  const problems = ctx.problems;
  if (!isConfigRef(n.on)) {
    problems.push('select: the first argument must be a config reference');
    return { select: '', discriminator: '', cases: {} };
  }
  const ref = configRefPath(n.on);
  const { entry, case: kase } = configRefTarget(n.on);
  ctx.useConfig(entry, `select '${ref}'`);
  if (kase !== undefined && !cases.some((c) => c.cond === kase))
    problems.push(
      `select '${ref}': the reference is narrowed to the case '${kase}' and can only be used inside it`,
    );
  const union = unionAt(n.on);
  if (!union)
    problems.push(
      `select '${ref}': it must reference a z.discriminatedUnion in config '${entry.name}'`,
    );
  else {
    for (const v of union.values)
      if (!Object.hasOwn(n.cases, v))
        problems.push(`select '${ref}': no case for ${union.discriminator} '${v}'`);
    for (const v of Object.keys(n.cases))
      if (!union.values.includes(v))
        problems.push(
          `select '${ref}': '${v}' is not a value of ${union.discriminator} (${union.values.join(', ')})`,
        );
  }
  const discriminator = union?.discriminator ?? '';
  const branches = Object.entries(n.cases).map(([value, nodes]) => {
    const inner = new Map(avail);
    const stages = describeStages(ctx, input, nodes, inner, [
      ...cases,
      { select: id, value, cond: `${ref}.${discriminator}=${value}` },
    ]);
    return { value, inner, stages };
  });
  // A step every case has is as available as in its cases; one only some cases have is maybe-available.
  const added = new Set(
    branches.flatMap((b) => [...b.inner.keys()].filter((k) => !avail.has(k))),
  );
  for (const name of added) {
    const present = branches
      .map((b) => b.inner.get(name))
      .filter((a): a is Avail => a !== undefined);
    avail.set(name, {
      model: present[0]!.model,
      maybe: present.length < branches.length || present.some((a) => a.maybe),
    });
  }
  return {
    select: ref,
    discriminator,
    cases: Object.fromEntries(branches.map((b) => [b.value, b.stages])),
  };
}
