import {
  bindingsCreate,
  bindingsGet,
  bindingsReconcile,
  bindingsSetBranch,
  bindingsSetPolling,
  bindingsStatus,
  bindingsUpdate,
  type PipelineBinding,
  type PipelineBindingStatus,
} from '@olve/api';
import { resolvePipeline } from '../address';
import { call } from '../api';
import { CliError } from '../errors';
import type { Output } from '../output';
import type { Command } from '../registry';
import { describeActivation, describePending } from './pipeline';

const utc = (iso: string) =>
  new Date(iso)
    .toISOString()
    .replace('T', ' ')
    .replace(/\.\d{3}Z$/, 'Z');

const reconcileText = (result: string, at: string | null | undefined) =>
  at ? `${result} (last sync ${utc(at)})` : result;

export function printBinding(out: Output, b: PipelineBinding): void {
  out.line(`Binding:     ${b.id}`);
  out.line(`Pipeline:    ${b.pipelineId}`);
  out.line(`Repo:        ${b.repo}@${b.branch} (${b.path})`);
  out.line(`Credentials: ${b.credentialsSecret ?? '(none)'}`);
  if (!b.polls) out.line('Polling:     off (syncs only on reconcile or its webhook)');
  out.line(`Last push:   ${b.lastPushSha ?? '(none)'}`);
  out.line(`Reconciled:  ${b.lastReconciledSha ?? '(none)'}`);
  out.line(`Reconcile:   ${reconcileText(b.status.result, b.status.lastSyncTime)}`);
  out.line(
    `  commit:    ${b.status.sha ?? '(none)'}${b.status.inProgress ? ' (in progress)' : ''}`,
  );
  out.line(
    `  version:   ${b.status.versionNumber != null ? `v${b.status.versionNumber}` : '(none)'}`,
  );
  for (const problem of b.status.problems) out.line(`  problem: ${problem}`);
  for (const warning of b.status.warnings ?? []) out.line(`  warning: ${warning}`);
}

export function printStatus(out: Output, s: PipelineBindingStatus): void {
  out.line(`Pipeline:    ${s.pipelineId}`);
  out.line(`Repo:        ${s.repo}@${s.branch} (${s.path})`);
  out.line(`Reconcile:   ${reconcileText(s.result, s.lastSyncTime)}`);
  out.line(
    `  commit:    ${s.reconcileSha ?? '(none)'}${s.reconcileInProgress ? ' (in progress)' : ''}`,
  );
  out.line(
    `  version:   ${s.reconciledVersion != null ? `v${s.reconciledVersion}` : '(none)'}`,
  );
  out.line(`Active:      ${describeActivation(s.activation) ?? '(no version)'}`);
  if (s.pending) out.line(`Pending:     ${describePending(s.pending)}`);
  out.line(`Last push:   ${s.lastPushSha ?? '(none)'}`);
  out.line(`Reconciled:  ${s.lastReconciledSha ?? '(none)'}`);
  if (s.problems.length) {
    out.line('Problems:');
    for (const problem of s.problems) out.line(`  - ${problem}`);
  }
  if (s.warnings.length) {
    out.line('Warnings:');
    for (const warning of s.warnings) out.line(`  - ${warning}`);
  }
}

export const bindingCommands: Command[] = [
  {
    noun: 'binding',
    verb: 'create',
    operands: 1,
    options: ['branch', 'credentials-secret', 'path'],
    summary: 'Create a pipeline bound to a repo (GitOps)',
    detail: `pl binding create <repo> [options]

  Creates a pipeline already bound to a git repo. The reconcile loop then builds the
  pipeline in the repo's binding path (.pipelines/pipeline.ts by default) into a version
  and activates it. <repo> is the GitHub "owner/name" slug.

  --branch <name>              Branch to track (default: main)
  --path <dir>                 Pipeline directory in the repo (default: .pipelines)
  --credentials-secret <key>   Key in the pipeline's k8s secret holding the GitHub token
                               (omit for a public repo)

  Example:
    pl binding create OliverVea/Olve.Pipelines --credentials-secret GITHUB_TOKEN`,
    async run(ctx) {
      const binding = await call(
        bindingsCreate({
          client: ctx.api,
          body: {
            repo: ctx.args.operand(0)!,
            branch: ctx.args.option('branch') ?? null,
            path: ctx.args.option('path') ?? null,
            credentialsSecret: ctx.args.option('credentials-secret') ?? null,
          },
        }),
      );
      ctx.out.emit(binding, () => printBinding(ctx.out, binding));
    },
  },
  {
    noun: 'binding',
    verb: 'get',
    operands: 1,
    summary: "Get a pipeline's GitOps binding",
    detail: 'pl binding get <pipeline>',
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const binding = await call(
        bindingsGet({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(binding, () => printBinding(ctx.out, binding));
    },
  },
  {
    noun: 'binding',
    verb: 'status',
    operands: 1,
    summary: "Show the binding's reconcile status and the resulting version",
    detail: 'pl binding status <pipeline>',
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const status = await call(
        bindingsStatus({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(status, () => printStatus(ctx.out, status));
    },
  },
  {
    noun: 'binding',
    verb: 'set-credentials',
    operands: 1,
    summary: "Set or clear the binding's credentials secret key",
    detail: `pl binding set-credentials <pipeline> [secretName]

  Names the key in the pipeline's secrets that holds the GitHub token used to read the
  repo (authenticated rate limit). Omit <secretName> to clear it.`,
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const binding = await call(
        bindingsUpdate({
          client: ctx.api,
          path: { pipelineId: id },
          body: { credentialsSecret: ctx.args.operand(1) ?? null },
        }),
      );
      ctx.out.emit(binding, () => printBinding(ctx.out, binding));
    },
  },
  {
    noun: 'binding',
    verb: 'set-branch',
    operands: 2,
    summary: 'Move the binding to another branch of the same repo',
    detail: `pl binding set-branch <pipeline> <branch>

  Keeps the pipeline's runs, versions and secrets. The next poll reconciles if the branch's
  newest config commit differs, and fires the push trigger if its head differs from the
  last push.`,
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const binding = await call(
        bindingsSetBranch({
          client: ctx.api,
          path: { pipelineId: id },
          body: { branch: ctx.args.operand(1)! },
        }),
      );
      ctx.out.emit(binding, () => printBinding(ctx.out, binding));
    },
  },
  {
    noun: 'binding',
    verb: 'set-polling',
    operands: 2,
    summary: "Turn the binding's polling of GitHub on or off",
    detail: `pl binding set-polling <pipeline> on|off

  Off, the binding stops polling GitHub for config commits and branch heads: it syncs only
  on \`pl binding reconcile\` or its webhook. For a pipeline driven from outside, such as a
  test fixture another pipeline reconciles and triggers.`,
    async run(ctx) {
      const text = ctx.args.operand(1)!;
      if (text !== 'on' && text !== 'off')
        throw new CliError(`Polling must be on or off, got '${text}'.`);
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const binding = await call(
        bindingsSetPolling({
          client: ctx.api,
          path: { pipelineId: id },
          body: { polls: text === 'on' },
        }),
      );
      ctx.out.emit(binding, () => printBinding(ctx.out, binding));
    },
  },
  {
    noun: 'binding',
    verb: 'reconcile',
    operands: 1,
    flags: ['rebuild'],
    summary:
      'Reconcile the bound config now (also retries a failed commit; --rebuild redoes an applied commit)',
    detail: `pl binding reconcile <pipeline> [--rebuild]

  --rebuild   Reconcile the current commit again although it is applied: after a server
              change in how manifests are read. The version changes only if the manifest does.`,
    async run(ctx) {
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      await call(
        bindingsReconcile({
          client: ctx.api,
          path: { pipelineId: id },
          query: { rebuild: ctx.args.flag('rebuild') || undefined },
        }),
      );
      if (!ctx.json)
        ctx.out.line(
          `Reconcile of ${label} queued; follow it with \`pl binding status ${label}\`.`,
        );
    },
  },
];
