import { STEP_NAME } from '../core/steps.ts';
import type { TriggerNode } from '../builder/pipeline.ts';
import type { TriggerT } from './manifest-schema.ts';
import type { DescribeContext } from './context.ts';

export function describeTriggers(
  ctx: DescribeContext,
  triggers: readonly TriggerNode[],
): TriggerT[] {
  const names = new Set<string>();
  return triggers.map((t) => {
    if (names.has(t.name))
      ctx.problems.push(
        `duplicate trigger '${t.name}'${t.kind === 'push' ? ' (onPush may be used once)' : ' (give each onPoll and onWebhook a distinct name)'}`,
      );
    names.add(t.name);
    if (t.kind === 'push') return { kind: 'push', name: 'push' };
    if (t.kind === 'webhook') return describeWebhook(ctx, t);
    return describePoll(ctx, t);
  });
}

function describeWebhook(
  ctx: DescribeContext,
  t: Extract<TriggerNode, { kind: 'webhook' }>,
): TriggerT {
  if (!STEP_NAME.test(t.name))
    ctx.problems.push(`webhook trigger name '${t.name}' must be lower-case kebab`);
  const secret = ctx.useSecret(t.verify.secret, `webhook '${t.name}'`);
  if (t.verify.scheme === 'bearer')
    return { kind: 'webhook', name: t.name, verify: { scheme: 'bearer', secret } };
  if (!t.verify.header)
    ctx.problems.push(`webhook '${t.name}': the signature header must not be empty`);
  return {
    kind: 'webhook',
    name: t.name,
    verify: {
      scheme: 'hmac-sha256',
      secret,
      header: t.verify.header,
      prefix: t.verify.prefix,
    },
  };
}

function describePoll(
  ctx: DescribeContext,
  t: Extract<TriggerNode, { kind: 'poll' }>,
): TriggerT {
  const problems = ctx.problems;
  if (!STEP_NAME.test(t.name))
    problems.push(`poll trigger name '${t.name}' must be lower-case kebab`);
  if (!(Number.isInteger(t.intervalSeconds) && t.intervalSeconds > 0))
    problems.push(`poll '${t.name}': intervalSeconds must be a positive integer`);
  const declared = new Set(t.secrets.map((s) => ctx.useSecret(s, `poll '${t.name}'`)));
  for (const [h, v] of Object.entries(t.headers)) {
    for (const m of v.matchAll(/\$SECRET:([A-Za-z0-9_]+)/g))
      if (!declared.has(m[1]!))
        problems.push(
          `poll '${t.name}': header '${h}' references secret '${m[1]}', which is not in the poll's secrets`,
        );
  }
  return {
    kind: 'poll',
    name: t.name,
    url: t.url,
    headers: { ...t.headers },
    secrets: [...declared].sort(),
    valuePath: t.valuePath,
    intervalSeconds: t.intervalSeconds,
  };
}
