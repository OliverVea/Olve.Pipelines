import {
  pipelinesDelete,
  pipelinesGet,
  pipelinesHealth,
  pipelinesList,
  versionsList,
  versionsSwitch,
  type PendingVersionInfo,
  type PipelineActivation,
  type PipelineHealth,
} from '@olve/api';
import { resolvePipeline } from '../address';
import { call } from '../api';
import { CliError } from '../errors';
import { short, time } from '../format';
import { table } from '../output';
import type { Command } from '../registry';

export function describeActivation(
  activation: PipelineActivation | null | undefined,
): string | undefined {
  if (!activation) return undefined;
  if (activation.type === 'active') return `active v${activation.versionNumber}`;
  return `draining v${activation.fromVersionNumber} -> v${activation.toVersionNumber}${activation.cancelRunning ? ' (cancelling running)' : ''}`;
}

/** The version runs start on: the active one, or the switch target while draining. */
export function currentVersion(
  activation: PipelineActivation | null | undefined,
): number | undefined {
  if (!activation) return undefined;
  return activation.type === 'active'
    ? activation.versionNumber
    : activation.toVersionNumber;
}

export function describePending(p: PendingVersionInfo): string {
  return (
    `${p.held ? 'held' : 'pending'} v${p.version}: ${p.reasons.join('; ')}` +
    (p.changedDefaults.length
      ? '; ' +
        p.changedDefaults
          .map((c) => `changes the default of ${c.path} (${c.from} → ${c.to})`)
          .join('; ')
      : '')
  );
}

const lastRun = (h: PipelineHealth) =>
  h.lastRun
    ? `#${h.lastRun.number} ${h.lastRun.status} ${time(h.lastRun.createdAt)}`
    : '-';

export function parseVersion(text: string, what = '--version'): number {
  const n = Number(text.replace(/^v/, ''));
  if (!/^v?\d+$/.test(text) || n < 1)
    throw new CliError(`${what} must be a positive integer (e.g. 7), got '${text}'.`);
  return n;
}

export const pipelineCommands: Command[] = [
  {
    noun: 'pipeline',
    verb: 'list',
    flags: ['summaries'],
    summary:
      'List pipelines with health, last run, active version, running/held runs and blocked steps',
    detail: `pl pipeline list [--summaries]

  --summaries   Also show the bound repo and description

Status is computed over runs (cancelled/superseded runs ignored; skipped steps are not faults):
  Healthy              nothing running; the latest settled run succeeded
  Running (Healthy)    a run is running; the latest settled run did not fail
  Running (Unhealthy)  a run is running; the latest settled run failed
  Unhealthy            nothing running; the latest settled run failed
  Idle                 no settled run and nothing running
Details: \`pl pipeline health <pipeline>\`.`,
    async run(ctx) {
      const pipelines = await call(pipelinesList({ client: ctx.api }));
      const detailed = ctx.args.flag('summaries');
      ctx.out.emit(pipelines, () => {
        const headers = [
          'ID',
          'NAME',
          'STATUS',
          'LAST RUN',
          'VERSION',
          'RUNNING/HELD',
          'BLOCKED',
        ];
        const rows = pipelines.map((p) => {
          const h = p.health;
          const common = [
            p.id,
            p.name,
            h.status,
            lastRun(h),
            describeActivation(h.activation) ?? '-',
            `${h.runningRuns}/${h.heldRuns.length}`,
            h.blockedSteps.length ? h.blockedSteps.join(',') : '-',
          ];
          return detailed ? [...common, p.repo ?? '-', p.description ?? ''] : common;
        });
        ctx.out.lines(
          table(detailed ? [...headers, 'REPO', 'DESCRIPTION'] : headers, rows),
        );
      });
    },
  },
  {
    noun: 'pipeline',
    verb: 'get',
    operands: 1,
    summary: 'Get a pipeline by id or name',
    detail: 'pl pipeline get <pipeline>   (id or name)',
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const pipeline = await call(
        pipelinesGet({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(pipeline, () => {
        ctx.out.line(`Id:   ${pipeline.id}`);
        ctx.out.line(`Name: ${pipeline.name}`);
      });
    },
  },
  {
    noun: 'pipeline',
    verb: 'delete',
    operands: 1,
    summary: 'Delete a pipeline (with its binding, runs and secrets)',
    detail: 'pl pipeline delete <pipeline>',
    async run(ctx) {
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      await call(pipelinesDelete({ client: ctx.api, path: { pipelineId: id } }));
      if (!ctx.json) ctx.out.line(`Deleted pipeline ${label} (${id}).`);
    },
  },
  {
    noun: 'pipeline',
    verb: 'health',
    operands: 1,
    summary: 'Run-based health: last run, running/held runs, blocked steps, activation',
    detail: `pl pipeline health <pipeline>

  Running if any run is running, else the latest succeeded/failed run's outcome
  (cancelled and superseded runs are ignored; skipped steps never count as unhealthy).`,
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const h = await call(
        pipelinesHealth({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(h, () => {
        ctx.out.line(`status:      ${h.status}`);
        ctx.out.line(`activation:  ${describeActivation(h.activation) ?? 'none'}`);
        if (h.pending) ctx.out.line(`pending:     ${describePending(h.pending)}`);
        ctx.out.line(
          `last run:    ${h.lastRun ? `#${h.lastRun.number} ${h.lastRun.status} (${time(h.lastRun.createdAt)})` : '-'}`,
        );
        ctx.out.line(
          `settled:     ${h.lastSettledRun ? `#${h.lastSettledRun.number} ${h.lastSettledRun.status} (${time(h.lastSettledRun.finishedAt)})` : '-'}`,
        );
        ctx.out.line(`running:     ${h.runningRuns}`);
        ctx.out.line(
          `blocked:     ${h.blockedSteps.length ? h.blockedSteps.join(', ') : '-'}`,
        );
        ctx.out.line(
          `held runs:   ${h.heldRuns.length ? h.heldRuns.map((r) => `#${r.number} at ${r.heldAt}`).join(', ') : '-'}`,
        );
      });
    },
  },
  {
    noun: 'pipeline',
    verb: 'versions',
    operands: 1,
    summary: "List a pipeline's versions and which one is active",
    detail: `pl pipeline versions <pipeline>

  Each version is a distinct manifest added by reconcile (linked to its commit). \`>\` marks
  the version runs start on. Steps/models list their entity versions (<id>.<version>).`,
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const versions = await call(
        versionsList({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(versions, () => {
        ctx.out.line(
          `activation: ${describeActivation(versions.activation) ?? 'none (no version yet)'}`,
        );
        ctx.out.line();
        const current = currentVersion(versions.activation);
        const rows = [...versions.versions]
          .sort((a, b) => b.number - a.number)
          .map((v) => [
            `${v.number === current ? '>' : ' '} v${v.number}`,
            short(v.commitSha),
            time(v.createdAt),
            Object.entries(v.stepVersions)
              .map(([step, n]) => `${step}.${n}`)
              .join(' '),
          ]);
        ctx.out.lines(table(['  VERSION', 'COMMIT', 'CREATED', 'STEPS'], rows));
      });
    },
  },
  {
    noun: 'pipeline',
    verb: 'switch',
    operands: 2,
    flags: ['cancel-running'],
    summary: 'Switch the active version (rollback = switch to an older one)',
    detail: `pl pipeline switch <pipeline> <version> [--cancel-running]

  The current version drains (starts no new runs; in-flight attempts finish), then <version>
  becomes active. Triggers during draining are queued and start on <version>.
  --cancel-running cancels the old version's in-flight attempts instead of waiting.`,
    async run(ctx) {
      const version = parseVersion(ctx.args.operand(1)!, 'Version');
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const activation = await call(
        versionsSwitch({
          client: ctx.api,
          path: { pipelineId: id },
          body: {
            version,
            cancelRunning: ctx.args.flag('cancel-running'),
          },
        }),
      );
      ctx.out.emit(activation, () =>
        ctx.out.line(describeActivation(activation) ?? 'none'),
      );
    },
  },
];
