import {
  pipelinesDelete,
  pipelinesGet,
  pipelinesHealth,
  pipelinesList,
  stepsReliability,
  versionsEffective,
  versionsGet,
  versionsList,
  versionsSwitch,
  type PendingVersionInfo,
  type PipelineActivation,
  type PipelineHealth,
  type ReliabilityReport,
  type StepReliability,
} from '@olve/api';
import { resolvePipeline } from '../address';
import { parseManifest } from '../dev/manifest';
import { renderManifest } from '../dev/render';
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

const percent = (rate: number) => `${(rate * 100).toFixed(1)}%`;

function describeWindow({ window: w }: ReliabilityReport): string {
  if (w.runs === 0) return 'Covers no runs yet.';
  const limit = w.limitedByRetention ? '; older runs were removed by retention' : '';
  return `Covers runs #${w.fromRun}..#${w.toRun} (${w.runs} of the latest ${w.requestedRuns}, ${time(w.from)} to ${time(w.to)})${limit}.`;
}

function describeNoReason({ runs: r }: ReliabilityReport): string {
  if (r.settled === 0) return 'No settled runs yet.';
  const which = r.runNumbers.length
    ? `: ${r.runNumbers.map((n) => `#${n}`).join(', ')}`
    : '';
  const absorbed = r.absorbed ? `; retries absorbed ${r.absorbed} more` : '';
  return `Failed for no real reason: ${r.failedForNoReason} of ${r.settled} settled runs (${percent(r.rate)})${which}${absorbed}.`;
}

const usual = (s: StepReliability) => {
  if (s.recentFailureRate == null || s.usualFailureRate == null) return '-';
  const rates = `${percent(s.recentFailureRate)} vs ${percent(s.usualFailureRate)}`;
  return s.aboveUsual ? `above usual (${rates})` : rates;
};

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
    verb: 'reliability',
    operands: 1,
    options: ['runs'],
    summary:
      "How reliable a pipeline's steps were: failure rates, flakiness, failures for no real reason",
    detail: `pl pipeline reliability <pipeline> [--runs <n>]

  Over the latest runs (default 100, at most 500; retention limits how far back they reach).
  A high failure rate alone is no defect: a test step fails on real problems.

  FAILED, RATE  Attempts that failed, split into OWN (the step reported it), INFRA (pod, image
                pull, launch, storage) and TIMEOUT
  LATEST/USUAL  The failure rate of the latest 10 attempts vs the attempts before them; marked
                "above usual" when a step clearly fails more than it usually does
  FLAKY         Own failures where the same code, config and input also succeeded
  REPRODUCED    Own failures where they failed again and never succeeded
  UNVERIFIED    Own failures that never ran again, so they can't be told apart

  A run failed for no real reason when a flaky, infrastructure or timeout failure stopped it,
  whether or not a rerun then rescued it. A flaky failure nobody reran isn't seen, so the
  rate is a lower bound. Reporting only: nothing fails or holds on these numbers.`,
    async run(ctx) {
      const runs = ctx.args.option('runs');
      if (runs !== undefined && !/^\d+$/.test(runs))
        throw new CliError('--runs must be a number.');
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const report = await call(
        stepsReliability({
          client: ctx.api,
          path: { pipelineId: id },
          query: { runs: runs === undefined ? undefined : Number(runs) },
        }),
      );
      ctx.out.emit(report, () => {
        ctx.out.line(describeWindow(report));
        ctx.out.line(describeNoReason(report));
        if (report.steps.length) {
          ctx.out.line();
          ctx.out.lines(
            table(
              [
                'STEP',
                'ATTEMPTS',
                'FAILED',
                'RATE',
                'OWN',
                'INFRA',
                'TIMEOUT',
                'FLAKY',
                'REPRODUCED',
                'UNVERIFIED',
                'LATEST/USUAL',
              ],
              report.steps.map((s) => [
                s.step,
                String(s.attempts),
                String(s.failures),
                percent(s.failureRate),
                String(s.ownFailures),
                String(s.infrastructureFailures),
                String(s.timeouts),
                String(s.flakyFailures),
                String(s.reproducedFailures),
                String(s.unverifiedFailures),
                usual(s),
              ]),
            ),
          );
        }
        for (const note of report.notes) ctx.out.line(note);
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
    verb: 'manifest',
    operands: 1,
    options: ['version'],
    flags: ['effective'],
    summary: "Show a version's manifest (default: the version runs start on)",
    detail: `pl pipeline manifest <pipeline> [--version <n>] [--effective] [--json]

  Summarizes the manifest like \`pl dev describe\` (input model, secrets, triggers, stages,
  \`when\` and \`select\` blocks). --json prints the canonical manifest JSON itself.
  --effective resolves each \`select\` with the pipeline's current config: the pipeline new
  runs execute.`,
    async run(ctx) {
      const text = ctx.args.option('version');
      const pipeline = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const pipelineId = pipeline.id;
      let number: number;
      if (text !== undefined) number = parseVersion(text, '--version');
      else {
        const versions = await call(
          versionsList({ client: ctx.api, path: { pipelineId } }),
        );
        const current = currentVersion(versions.activation);
        if (current === undefined)
          throw new CliError(`Pipeline '${pipeline.label}' has no active version yet.`);
        number = current;
      }
      const version = await call(
        versionsGet({ client: ctx.api, path: { pipelineId, number } }),
      );
      let manifest = version.manifest;
      let selection: string | undefined;
      if (ctx.args.flag('effective')) {
        const effective = await call(
          versionsEffective({ client: ctx.api, path: { pipelineId, number } }),
        );
        manifest = effective.manifest;
        const picked = Object.entries(effective.selected).map(([k, v]) => `${k} = ${v}`);
        selection = `effective with config #${effective.configSnapshot ?? '-'}${picked.length ? `: ${picked.join(', ')}` : ''}`;
      }
      if (ctx.json) {
        ctx.out.line(manifest);
        return;
      }
      ctx.out.line(
        `v${version.number} · ${version.manifestHash} · commit ${short(version.commitSha)} · ${time(version.createdAt)}`,
      );
      if (selection) ctx.out.line(selection);
      ctx.out.line();
      renderManifest((line) => ctx.out.line(line), parseManifest(manifest));
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
