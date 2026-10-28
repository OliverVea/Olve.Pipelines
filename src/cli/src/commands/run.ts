import {
  runsCancel,
  runsContinue,
  runsCriticalPath,
  runsGet,
  runsList,
  runsRerun,
  runsStart,
  type CriticalPathLead,
  type Run,
  type RunStart,
  type RunStep,
  type RunSummary,
} from '@olve/api';
import { formatAddress, resolvePipeline, resolveRun } from '../address';
import { call } from '../api';
import { CliError } from '../errors';
import { duration, json, short, time } from '../format';
import { buildInput, pipelineInputSchema } from '../input';
import { Output, table } from '../output';
import type { Command } from '../registry';

export function printOutcome(out: Output, label: string, start: RunStart): void {
  const run = start.run;
  if (start.outcome === 'started' && run) {
    out.line(formatAddress(label, run.number));
    out.line(`started run #${run.number} on v${run.versionNumber} (${run.versionStamp})`);
  } else if (start.outcome === 'deduplicated' && run) {
    out.line(formatAddress(label, run.number));
    out.line(`deduplicated: run #${run.number} already has this input (${run.status})`);
  } else if (start.outcome === 'queued') {
    out.line(
      'queued: the pipeline is switching versions; the run starts when the switch completes',
    );
  } else out.line(start.outcome);
}

export function note(r: RunSummary): string {
  const notes: string[] = [];
  if (r.heldAt) notes.push(`held at ${r.heldAt}`);
  if (r.supersededByNumber) notes.push(`superseded by #${r.supersededByNumber}`);
  if (r.rerunOfNumber) notes.push(`rerun of #${r.rerunOfNumber}`);
  return notes.join('; ');
}

function stepNote(s: RunStep): string {
  const notes: string[] = [];
  if (s.blocked) notes.push('blocked');
  if (s.succeededAttempt !== null && s.succeededAttempt !== s.latestAttempt)
    notes.push(`uses @${s.succeededAttempt}`);
  if (s.reason) notes.push(s.reason);
  return notes.join('; ');
}

export function printRun(out: Output, label: string, d: Run): void {
  out.line(`${formatAddress(label, d.number)}  ${d.status}`);
  out.line(
    `version:   v${d.versionNumber}${d.configSnapshot != null ? `, config #${d.configSnapshot}` : ''} · ${d.versionStamp} · commit ${short(d.commitSha)}`,
  );
  out.line(
    `trigger:   ${d.triggerKind} · created ${time(d.createdAt)} · finished ${time(d.finishedAt)} · took ${duration(d.createdAt, d.finishedAt)}`,
  );
  out.line(`input:     ${json(d.input)}`);
  if (d.config != null) out.line(`config:    ${d.config}`);
  const n = note(d);
  if (n) out.line(`note:      ${n}`);
  out.line(
    `continue:  ${d.continuable ? 'possible' : 'no (not on the active version, or superseded)'}`,
  );

  if (d.stages.length === 0) {
    out.line();
    out.line(
      "(stage layout unavailable: the pinned version's manifest could not be read)",
    );
    return;
  }
  for (const stage of d.stages) {
    out.line();
    const when = stage.when.length
      ? '  when ' +
        stage.when
          .map((c) => `${c.field} in [${c.values.map((v) => json(v)).join(', ')}]`)
          .join(' and ')
      : '';
    out.line(`stage ${stage.index + 1}${when}`);
    out.lines(
      table(
        ['  STEP', 'STATUS', 'ATTEMPT', 'TOOK', 'NOTE'],
        stage.steps.map((s) => [
          '  ' + s.name,
          s.status,
          s.latestAttempt != null ? `@${s.latestAttempt}/${s.attemptCount}` : '-',
          duration(s.startedAt, s.finishedAt),
          stepNote(s),
        ]),
      ),
    );
  }
}

const seconds = (s: number) =>
  duration('1970-01-01T00:00:00Z', new Date(Math.max(0, s) * 1000).toISOString());

function positive(text: string | undefined, option: string): number | undefined {
  if (text === undefined) return undefined;
  const n = Number(text.replace(/^[#v]/, ''));
  if (!/^[#v]?\d+$/.test(text) || n < 1)
    throw new CliError(`--${option} must be a positive number.`);
  return n;
}

export const runCommands: Command[] = [
  {
    noun: 'trigger',
    operands: 1,
    options: ['field', 'input'],
    summary: 'Start a manual run with an input; prints the run address',
    detail: `pl trigger <pipeline> [--input <json|@file|@->] [--field k=v ...]

  Starts a run on the pipeline's current version. The body is the pipeline input: --input
  gives a whole JSON object (inline, @file, or @- for stdin) and each --field sets one
  top-level field over it, typed by the version's input model (sha=0123 stays a string).
  The service fills defaults and validates; problems are listed, no run is started.
  <pipeline> is a pipeline id or name.

  Prints the run address (<pipeline>#<run>) — use it with \`pl run get\` / \`pl step ...\`.

  Examples:
    pl trigger olve-pipelines --field deployTo=beta --field sha=65bdc9171d3c0f1e8a2b4c6d8e0f1a2b3c4d5e6f
    pl trigger olve-pipelines --input '{"deployTo":"beta","sha":"65bdc9171d3c0f1e8a2b4c6d8e0f1a2b3c4d5e6f"}'`,
    async run(ctx) {
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const fields = ctx.args.options('field');
      const schema = fields.length ? await pipelineInputSchema(ctx, id) : undefined;
      const input = await buildInput(ctx.args.option('input'), fields, schema, ctx.stdin);
      const start = await call(
        runsStart({ client: ctx.api, path: { pipelineId: id }, body: input }),
      );
      ctx.out.emit(start, () => printOutcome(ctx.out, label, start));
    },
  },
  {
    noun: 'run',
    verb: 'list',
    operands: 1,
    options: ['page', 'page-size'],
    summary: "List a pipeline's runs (newest first)",
    detail: `pl run list <pipeline> [--page <n>] [--page-size <n>]

  --page <n>        Page number (default: 0)
  --page-size <n>   Page size (default: 20)`,
    async run(ctx) {
      const page = Number(ctx.args.optionOr('page', '0'));
      if (!/^\d+$/.test(ctx.args.optionOr('page', '0')))
        throw new CliError('--page must be a non-negative integer.');
      const pageSize = Number(ctx.args.optionOr('page-size', '20'));
      if (!/^\d+$/.test(ctx.args.optionOr('page-size', '20')) || pageSize < 1)
        throw new CliError('--page-size must be a positive integer.');
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const runs = await call(
        runsList({
          client: ctx.api,
          path: { pipelineId: id },
          query: { offset: page * pageSize, limit: pageSize },
        }),
      );
      ctx.out.emit(runs, () => {
        ctx.out.lines(
          table(
            ['RUN', 'STATUS', 'TRIGGER', 'VERSION', 'CREATED', 'TOOK', 'NOTE'],
            runs.items.map((r) => [
              `#${r.number}`,
              r.status,
              r.triggerKind,
              `v${r.versionNumber}`,
              time(r.createdAt),
              duration(r.createdAt, r.finishedAt),
              note(r),
            ]),
          ),
        );
        ctx.out.line();
        ctx.out.line(`page ${page} · ${runs.items.length} of ${runs.total} run(s)`);
      });
    },
  },
  {
    noun: 'run',
    verb: 'get',
    operands: 1,
    summary: 'Show a run: input, version, each stage and step',
    detail: `pl run get <pipeline>#<run>

  Step statuses without an attempt: held (the step is blocked and holds the run here), pending
  (not reached yet), not-run (the run finished without it).`,
    async run(ctx) {
      const { id, label, address } = await resolveRun(ctx.api, ctx.args.operand(0)!);
      const run = await call(
        runsGet({ client: ctx.api, path: { pipelineId: id, number: address.run } }),
      );
      ctx.out.emit(run, () => printRun(ctx.out, label, run));
    },
  },
  {
    noun: 'run',
    verb: 'cancel',
    operands: 1,
    summary: 'Cancel a run (also its scheduled and in-progress attempts)',
    detail:
      "pl run cancel <pipeline>#<run>   Cancels the run; in-progress attempts' Jobs are deleted.",
    async run(ctx) {
      const { id, label, address } = await resolveRun(ctx.api, ctx.args.operand(0)!);
      const run = await call(
        runsCancel({ client: ctx.api, path: { pipelineId: id, number: address.run } }),
      );
      ctx.out.emit(run, () =>
        ctx.out.line(`${formatAddress(label, run.number)}  ${run.status}`),
      );
    },
  },
  {
    noun: 'run',
    verb: 'rerun',
    operands: 1,
    summary: 'Rerun a whole run: a new run with the same input (prints its address)',
    detail:
      'pl run rerun <pipeline>#<run>   A new run with the same input on the current version, linked via rerunOf.',
    async run(ctx) {
      const { id, label, address } = await resolveRun(ctx.api, ctx.args.operand(0)!);
      const start = await call(
        runsRerun({ client: ctx.api, path: { pipelineId: id, number: address.run } }),
      );
      ctx.out.emit(start, () => printOutcome(ctx.out, label, start));
    },
  },
  {
    noun: 'run',
    verb: 'continue',
    operands: 1,
    options: ['field', 'input'],
    summary: 'Continue a run, optionally changing input fields (promote what you tested)',
    detail: `pl run continue <pipeline>#<run> [--field k=v ...] [--input <json|@file|@->]

  Keeps the completed stages and re-evaluates \`when\` for the rest. Changed fields are merged
  over the run's stored input and validated against its pinned input model; a field a
  completed step already consumed cannot change (e.g. sha).

  Example: pl run continue olve-pipelines#412 --field deployTo=prod`,
    async run(ctx) {
      const { id, label, address } = await resolveRun(ctx.api, ctx.args.operand(0)!);
      const fields = ctx.args.options('field');
      let changes: Record<string, unknown> | undefined;
      if (fields.length || ctx.args.option('input') !== undefined) {
        // Type --field values by the run's *pinned* input model.
        let schema;
        if (fields.length) {
          const run = await call(
            runsGet({ client: ctx.api, path: { pipelineId: id, number: address.run } }),
          ).catch(() => undefined);
          if (run) schema = await pipelineInputSchema(ctx, id, run.versionNumber);
        }
        changes = await buildInput(ctx.args.option('input'), fields, schema, ctx.stdin);
      }
      const run = await call(
        runsContinue({
          client: ctx.api,
          path: { pipelineId: id, number: address.run },
          body: changes ? { input: changes } : {},
        }),
      );
      ctx.out.emit(run, () =>
        ctx.out.line(
          `${formatAddress(label, run.number)}  ${run.status} (input ${json(run.input)})`,
        ),
      );
    },
  },
  {
    noun: 'run',
    verb: 'critical-path',
    operands: 1,
    options: ['from', 'to', 'step', 'since-version'],
    flags: ['all'],
    summary:
      "The critical path of a range of runs: each step's time on it and share of it",
    detail: `pl run critical-path <pipeline> [--from <run> | --since-version <v>] [--to <run>] [--step <name>] [--all]

  Stages are barriers, so each stage's critical step is the one that finished last; it is
  charged from the stage's first attempt to that finish (WAIT: how much of it was spent
  before the step's first attempt started). Automatic retries count, later reruns don't.

  --from, --to   Run numbers (default: the latest 20 runs; at most 500)
  --since-version <v>  From the first run on pipeline version v or later (instead of --from)
  --step <name>  Also list the runs that step was on the critical path in
  --all          Include failed and cancelled runs (default: succeeded only)

  LEAD is the time from the push (or from when the server noticed it) to the first step:
  detect (push to webhook or poll), check (branch head and config applied, a reconcile
  included), map (the map-trigger Job) and start. Automatic runs only.

  AVG OVERHEAD is the average part of a step's time on the path outside its step container
  once it was picked up (pod start, fetching, image pull, noticing the end, collecting the
  result; WAIT is separate), over the runs that recorded it; "-" when none did.`,
    async run(ctx) {
      const from = positive(ctx.args.option('from'), 'from');
      const to = positive(ctx.args.option('to'), 'to');
      const sinceVersion = positive(ctx.args.option('since-version'), 'since-version');
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const report = await call(
        runsCriticalPath({
          client: ctx.api,
          path: { pipelineId: id },
          query: {
            from,
            to,
            includeUnsuccessful: ctx.args.flag('all') || undefined,
            sinceVersion,
          },
        }),
      );
      const step = ctx.args.option('step');
      ctx.out.emit(report, () => {
        if (report.runs.length === 0) {
          ctx.out.line(`(no runs to report in #${report.from}..#${report.to})`);
          for (const skip of report.skipped)
            ctx.out.line(`#${skip.number}: ${skip.reason}`);
          return;
        }
        ctx.out.lines(
          table(
            ['RUN', 'STATUS', 'LEAD', 'PATH', 'IDLE', 'CRITICAL PATH'],
            report.runs.map((r) => [
              `#${r.number}`,
              r.status,
              r.lead ? seconds(r.lead.seconds) : '-',
              seconds(r.seconds),
              r.idleSeconds >= 1 ? seconds(r.idleSeconds) : '-',
              r.path
                .map(
                  (p) =>
                    `${p.step}${p.attempts > 1 ? `×${p.attempts}` : ''} ${seconds(p.seconds)}`,
                )
                .join(' → '),
            ]),
          ),
        );
        ctx.out.line();
        const known = report.runs.filter((r) => r.activeSeconds != null);
        const overhead = known.length
          ? `; overhead ${seconds(known.reduce((sum, r) => sum + r.seconds - r.path.reduce((w, p) => w + p.waitSeconds, 0) - r.activeSeconds!, 0) / known.length)} per run (${known.length} with timings)`
          : '';
        ctx.out.line(
          `${report.runs.length} run(s) in #${report.from}..#${report.to}: ${seconds(report.totalSeconds)} on the critical path, ${seconds(report.averageSeconds)} per run${overhead}`,
        );
        const leads = report.runs.flatMap((r) => (r.lead ? [r.lead] : []));
        if (leads.length) {
          const part = (pick: (l: CriticalPathLead) => number | null | undefined) => {
            const values = leads.map(pick).filter((v): v is number => v != null);
            return values.length
              ? seconds(values.reduce((a, b) => a + b, 0) / values.length)
              : '?';
          };
          ctx.out.line(
            `lead (push to first step), average of ${leads.length}: ${seconds(leads.reduce((a, l) => a + l.seconds, 0) / leads.length)} = detect ${part((l) => l.detect)} + check ${part((l) => l.check)} + map ${part((l) => l.map)} + start ${part((l) => l.start)}`,
          );
        }
        ctx.out.line();
        ctx.out.lines(
          table(
            ['STEP', 'ON PATH', 'TOTAL', 'SHARE', 'AVG WHEN ON', 'WAIT', 'AVG OVERHEAD'],
            report.steps.map((s) => [
              s.step,
              `${s.runs}/${report.runs.length}`,
              seconds(s.seconds),
              `${(s.share * 100).toFixed(1)}%`,
              seconds(s.averageSeconds),
              seconds(s.waitSeconds),
              s.overheadSeconds != null ? seconds(s.overheadSeconds) : '-',
            ]),
          ),
        );
        const notes = [
          ...report.runs.flatMap((r) => r.notes.map((n) => `#${r.number}: ${n}`)),
          ...report.skipped.map((s) => `#${s.number}: not reported (${s.reason})`),
        ];
        if (notes.length) {
          ctx.out.line();
          ctx.out.lines(notes);
        }
        if (step !== undefined) {
          const runs = report.steps.find((s) => s.step === step)?.runNumbers ?? [];
          const onPath = new Map(
            report.runs.map((r) => [
              r.number,
              r.path
                .filter((p) => p.step === step)
                .reduce((sum, p) => sum + p.seconds, 0),
            ]),
          );
          ctx.out.line();
          ctx.out.line(
            `${step} was on the critical path in ${runs.length}/${report.runs.length} run(s)` +
              (runs.length
                ? ': ' +
                  runs.map((n) => `#${n} (${seconds(onPath.get(n) ?? 0)})`).join(', ')
                : ''),
          );
        }
      });
    },
  },
];
