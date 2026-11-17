import { renameSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  ciPullsGet,
  ciPullsList,
  ciPullsRun,
  ciQueueDequeue,
  ciQueueEnqueue,
  ciQueueGet,
  ciRunsCancel,
  ciRunsGet,
  ciRunsReliability,
  ciRunStepsFiles,
  ciRunStepsLogs,
  ciRunStepsOutput,
  type CiAttemptRef,
  type CiReliabilityReport,
  type CiRun,
  type CiRunStep,
  type CiRunStepCell,
  type CiRunSummary,
  type CiStepState,
  type Pull,
  type QueueEntry,
} from '@olve/api';
import { ciLogEvents } from '@olve/client';
import {
  formatCiAddress,
  parsePull,
  resolvePipeline,
  resolveRun,
  resolveStep,
} from '../address';
import { call, downloadStepFile, streamError } from '../api';
import { CliError, describe } from '../errors';
import { duration, indented, json, short, time } from '../format';
import { Output, table } from '../output';
import type { Command, CommandContext } from '../registry';
import { size } from './step';

const PASSED = ['succeeded', 'skipped', 'excluded'];

/** Steps passed (succeeded, skipped or excluded) out of all: `3/5`. */
export function stepCounts(steps: readonly CiStepState[]): string {
  if (steps.length === 0) return '-';
  return `${steps.filter((s) => PASSED.includes(s.status)).length}/${steps.length}`;
}

export function runNote(r: CiRunSummary): string {
  const notes: string[] = [];
  const failed = r.steps.filter((s) => s.status === 'failed').map((s) => s.name);
  if (failed.length) notes.push(`failed: ${failed.join(', ')}`);
  if (r.supersededByNumber != null)
    notes.push(`superseded by #ci${r.supersededByNumber}`);
  if (r.cancelReason) notes.push(`cancelled: ${r.cancelReason}`);
  return notes.join('; ');
}

export function pullNote(p: Pull): string {
  const notes: string[] = [];
  if (p.state !== 'open') notes.push(p.state);
  if (p.draft) notes.push('draft');
  if (p.conflict) notes.push('conflict');
  if (p.stale) notes.push('stale');
  if (p.queue?.position != null) notes.push(`queued #${p.queue.position}`);
  else if (p.queue) notes.push(`queue: ${p.queue.state}`);
  const run = p.latestRun ? runNote(p.latestRun) : '';
  if (run) notes.push(run);
  return notes.join('; ');
}

export function pullRows(pulls: readonly Pull[]): string[] {
  return table(
    ['PR', 'STATUS', 'STEPS', 'RUN', 'COMMIT', 'STARTED', 'TOOK', 'TITLE', 'NOTE'],
    pulls.map((p) => {
      const r = p.latestRun;
      return [
        `#${p.number}`,
        r?.status ?? '-',
        r ? stepCounts(r.steps) : '-',
        r ? `#ci${r.number}` : '-',
        short(p.headSha),
        time(r?.createdAt),
        duration(r?.createdAt, r?.finishedAt),
        p.title,
        pullNote(p),
      ];
    }),
  );
}

export function runRows(runs: readonly CiRunSummary[]): string[] {
  return table(
    ['RUN', 'KIND', 'STATUS', 'STEPS', 'TESTED', 'MAIN', 'STARTED', 'TOOK', 'NOTE'],
    runs.map((r) => [
      `#ci${r.number}`,
      r.kind === 'queue' ? `queue (${r.pulls.map((n) => `#${n}`).join(' ')})` : r.kind,
      r.status,
      stepCounts(r.steps),
      short(r.testedSha),
      short(r.mainSha),
      time(r.createdAt),
      duration(r.createdAt, r.finishedAt),
      runNote(r),
    ]),
  );
}

/** A queue entry's state: its place while queued, the run testing it, why it was removed. */
export function queueState(e: QueueEntry): string {
  if (e.state === 'queued' && e.position != null) return `queued #${e.position}`;
  if (e.state === 'testing' && e.runs.length) return `testing in #ci${e.runs[0]}`;
  if (e.state === 'removed' && e.reason)
    return `removed (${e.reason}${e.failedStep ? `: ${e.failedStep}` : ''})`;
  return e.state;
}

export function queueRows(entries: readonly QueueEntry[], finished: boolean): string[] {
  return table(
    ['PR', 'STATE', 'RUNS', 'VIA', finished ? 'FINISHED' : 'ENQUEUED', 'TITLE', 'NOTE'],
    entries.map((e) => [
      `#${e.pull}`,
      e.state === 'queued' && e.position != null ? `queued #${e.position}` : e.state,
      e.runs.length ? e.runs.map((n) => `#ci${n}`).join(' ') : '-',
      e.via === 'api' && e.enqueuedBy ? `api (${e.enqueuedBy})` : e.via,
      time(finished ? e.finishedAt : e.enqueuedAt),
      e.title,
      e.reason ? `${e.reason}${e.failedStep ? `: ${e.failedStep}` : ''}` : '',
    ]),
  );
}

const reused = (label: string, ref: CiAttemptRef | null | undefined) =>
  ref ? `reused ${formatCiAddress(label, ref.run, ref.step, ref.attempt)}` : '';

function stepNote(label: string, s: CiRunStep | CiRunStepCell): string {
  const notes: string[] = [];
  const from = reused(label, s.reusedFrom);
  if (from) notes.push(from);
  if (s.succeededAttempt != null && s.succeededAttempt !== s.latestAttempt)
    notes.push(`uses @${s.succeededAttempt}`);
  if (s.reason) notes.push(s.reason);
  for (const l of s.links ?? [])
    notes.push(`→ ${l.url}${l.label ? ` (${l.label})` : ''}`);
  return notes.join('; ');
}

const attemptOf = (s: CiRunStep | CiRunStepCell) =>
  s.latestAttempt != null ? `@${s.latestAttempt}/${s.attemptCount}` : '-';

const cellsDone = (cells: CiRunStepCell[]) =>
  `${cells.filter((c) => c.status === 'succeeded' || c.status === 'skipped').length}/${cells.length} cells`;

export function printCiRun(out: Output, label: string, r: CiRun): void {
  out.line(`${formatCiAddress(label, r.number)}  ${r.status}`);
  out.lines(
    r.pullHeads.map(
      (p) => `pull:      #${p.number} ${p.title} (${p.branch} at ${short(p.headSha)})`,
    ),
  );
  out.line(
    `tested:    ${short(r.testedSha)} (tree ${short(r.tree)}) on main ${short(r.mainSha)} · definition ${r.definitionSha === r.mainSha ? "main's" : `built from ${short(r.definitionSha)}`}`,
  );
  out.line(
    `kind:      ${r.kind} · created ${time(r.createdAt)} · finished ${time(r.finishedAt)} · took ${duration(r.createdAt, r.finishedAt)}`,
  );
  const n = runNote(r);
  if (n) out.line(`note:      ${n}`);
  for (const problem of r.problems) out.line(`problem:   ${problem}`);

  if (r.stages.length === 0) {
    if (r.problems.length === 0) {
      out.line();
      out.line('(the definition is not built and described yet)');
    }
    return;
  }
  for (const stage of r.stages) {
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
        stage.steps.flatMap((s) => [
          [
            '  ' + s.name,
            s.status,
            s.cells ? cellsDone(s.cells) : attemptOf(s),
            duration(s.startedAt, s.finishedAt),
            stepNote(label, s),
          ],
          ...(s.cells ?? []).map((c) => [
            '    ' + c.name,
            c.status,
            attemptOf(c),
            duration(c.startedAt, c.finishedAt),
            stepNote(label, c),
          ]),
        ]),
      ),
    );
  }
}

/** Follows a CI attempt's log stream until it ends; reconnects on a lost connection. */
async function followLog(
  ctx: CommandContext,
  addr: Awaited<ReturnType<typeof resolveStep>>,
): Promise<void> {
  const path = { pipelineId: addr.id, number: addr.address.run, step: addr.address.step };
  let printed = '';
  for (;;) {
    let ended = false;
    try {
      for await (const event of ciLogEvents(
        ctx.api,
        ctx.signal,
        path,
        addr.address.attempt,
      )) {
        if (event.type === 'log') {
          const text = event.reset ? event.text : printed + event.text;
          if (event.reset && !event.text) continue; // a reconnect opens with an empty reset
          if (text.startsWith(printed)) ctx.out.write(text.slice(printed.length));
          // The source changed (init container -> step container, or the stored log replaced the live one).
          else ctx.out.write((printed && !printed.endsWith('\n') ? '\n' : '') + text);
          printed = text;
        } else if (event.type === 'end') {
          if (printed && !printed.endsWith('\n')) ctx.out.write('\n');
          ctx.log(`attempt ${event.status}`);
          ended = true;
          break;
        }
      }
    } catch (error) {
      if (ctx.signal.aborted) return;
      const failure = error instanceof CliError ? error : streamError(error, 'The log');
      if (!failure.retryable) {
        // No attempt yet: keep waiting only while the run can still reach the step.
        const run = await call(
          ciRunsGet({
            client: ctx.api,
            path: { pipelineId: addr.id, number: addr.address.run },
          }),
        );
        const names = run.stages
          .flatMap((st) => st.steps)
          .flatMap((st) => [st.name, ...(st.cells ?? []).map((c) => c.name)]);
        if (run.stages.length && !names.includes(addr.address.step))
          throw new CliError(
            `CI run #ci${run.number} has no step '${addr.address.step}'.`,
          );
        if (run.status !== 'Running')
          throw new CliError(
            `CI run #ci${run.number} is ${run.status} and has no ${addr.address.attempt !== undefined ? `attempt ${addr.address.attempt}` : 'attempt'} of step '${addr.address.step}'.`,
          );
      }
      ctx.log(`log stream: ${describe(failure)}`);
    }
    if (ended || ctx.signal.aborted) return;
    await ctx.sleep(logRetryMs.value, ctx.signal);
  }
}

/** Between reconnects of `ci logs --follow`; tests shorten it. */
export const logRetryMs = { value: 2_000 };

export const ciCommands: Command[] = [
  {
    noun: 'ci',
    verb: 'ls',
    operands: 1,
    flags: ['closed'],
    options: ['page', 'page-size'],
    summary: "List a pipeline's pull requests with their latest CI run",
    detail: `pl ci ls <pipeline> [--closed] [--page <n>] [--page-size <n>]

  Open pull requests into the binding's branch, newest first (--closed: closed and merged
  ones). Per PR: the latest CI run's status, its steps passed out of all, the run (address
  it as <pipeline>#ci<run>), the PR's head commit, when the run started and how long it took.
  NOTE says draft, conflict (no run until a push lets it merge), stale (tested on a main
  that has moved since) and the failed steps.`,
    async run(ctx) {
      const page = Number(ctx.args.optionOr('page', '0'));
      if (!/^\d+$/.test(ctx.args.optionOr('page', '0')))
        throw new CliError('--page must be a non-negative integer.');
      const pageSize = Number(ctx.args.optionOr('page-size', '20'));
      if (!/^\d+$/.test(ctx.args.optionOr('page-size', '20')) || pageSize < 1)
        throw new CliError('--page-size must be a positive integer.');
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const closed = ctx.args.flag('closed');
      const pulls = await call(
        ciPullsList({
          client: ctx.api,
          path: { pipelineId: id },
          query: {
            state: closed ? 'closed' : 'open',
            offset: page * pageSize,
            limit: pageSize,
          },
        }),
      );
      ctx.out.emit(pulls, () => {
        if (pulls.total === 0) {
          ctx.out.line(closed ? '(no closed pull requests)' : '(no open pull requests)');
          return;
        }
        ctx.out.lines(pullRows(pulls.items));
        ctx.out.line();
        ctx.out.line(
          `page ${page} · ${pulls.items.length} of ${pulls.total} pull request(s)`,
        );
      });
    },
  },
  {
    noun: 'ci',
    verb: 'show',
    operands: 2,
    summary: 'Show a pull request and its CI runs (newest first)',
    detail: `pl ci show <pipeline> <pr>

  <pr> is the pull request's number (12 or #12). Shows its head, state, whether it conflicts
  or its result is stale, its place in the merge queue, and every CI run that tested it.`,
    async run(ctx) {
      const number = parsePull(ctx.args.operand(1)!);
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const pull = await call(
        ciPullsGet({ client: ctx.api, path: { pipelineId: id, pull: number } }),
      );
      ctx.out.emit(pull, () => {
        ctx.out.line(`${label} #${pull.number}  ${pull.title}`);
        ctx.out.line(
          `branch:    ${pull.branch} at ${short(pull.headSha)} · ${pull.state}${pull.draft ? ' (draft)' : ''} · updated ${time(pull.updatedAt)}`,
        );
        const flags = [
          pull.conflict ? "conflict: GitHub can't merge it onto main" : '',
          pull.stale ? 'stale: tested on a main that has moved since' : '',
        ].filter(Boolean);
        for (const f of flags) ctx.out.line(`note:      ${f}`);
        if (pull.queue)
          ctx.out.line(
            `queue:     ${pull.queue.state}${pull.queue.position != null ? ` #${pull.queue.position}` : ''}${pull.queue.reason ? ` (${pull.queue.reason})` : ''}`,
          );
        ctx.out.line();
        if (pull.runs.length === 0) ctx.out.line('(no CI runs)');
        else ctx.out.lines(runRows(pull.runs));
      });
    },
  },
  {
    noun: 'ci',
    verb: 'get',
    operands: 1,
    summary: 'Show a CI run: what it tested, each stage and step, reused steps',
    detail: `pl ci get <pipeline>#ci<run>

  CI runs are numbered per pipeline in a sequence of their own, so they are addressed
  <pipeline>#ci<run> (<pipeline>#<run> is a deploy run). A step reused from an earlier run
  on the same tree names the attempt it reuses.`,
    async run(ctx) {
      const { id, label, address } = await resolveRun(
        ctx.api,
        ctx.args.operand(0)!,
        'ci',
      );
      const run = await call(
        ciRunsGet({ client: ctx.api, path: { pipelineId: id, number: address.run } }),
      );
      ctx.out.emit(run, () => printCiRun(ctx.out, label, run));
    },
  },
  {
    noun: 'ci',
    verb: 'cancel',
    operands: 1,
    summary: 'Cancel a CI run (also its scheduled and in-progress attempts)',
    detail:
      "pl ci cancel <pipeline>#ci<run>   Cancels the CI run; in-progress attempts' Jobs are deleted.",
    async run(ctx) {
      const { id, label, address } = await resolveRun(
        ctx.api,
        ctx.args.operand(0)!,
        'ci',
      );
      const run = await call(
        ciRunsCancel({ client: ctx.api, path: { pipelineId: id, number: address.run } }),
      );
      ctx.out.emit(run, () =>
        ctx.out.line(`${formatCiAddress(label, run.number)}  ${run.status}`),
      );
    },
  },
  {
    noun: 'ci',
    verb: 'rerun',
    operands: 2,
    summary: "Run a pull request's CI again (prints the new run's address)",
    detail: `pl ci rerun <pipeline> <pr>

  A new CI run of the pull request's head; steps that already succeeded on the same tree are
  reused, so only the rest run. It supersedes the PR's running run.`,
    async run(ctx) {
      const number = parsePull(ctx.args.operand(1)!);
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const run = await call(
        ciPullsRun({ client: ctx.api, path: { pipelineId: id, pull: number } }),
      );
      ctx.out.emit(run, () => {
        ctx.out.line(formatCiAddress(label, run.number));
        ctx.out.line(`started CI run #ci${run.number} of #${number} (${run.status})`);
      });
    },
  },
  {
    noun: 'ci',
    verb: 'merge',
    operands: 2,
    summary: 'Add a pull request to the merge queue',
    detail: `pl ci merge <pipeline> <pr>

  The queue tests the pull request rebased onto the current main (in a batch with the ones
  ahead of it) and merges it when that succeeds. Refused for a draft, a pull request that
  conflicts with main and one from a fork. Wait for it with \`pl wait ci <pipeline> <pr> --queue\`.`,
    async run(ctx) {
      const number = parsePull(ctx.args.operand(1)!);
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const entry = await call(
        ciQueueEnqueue({
          client: ctx.api,
          path: { pipelineId: id },
          body: { pull: number },
        }),
      );
      ctx.out.emit(entry, () =>
        ctx.out.line(`${label} #${entry.pull}  ${queueState(entry)}`),
      );
    },
  },
  {
    noun: 'ci',
    verb: 'dequeue',
    operands: 2,
    summary: 'Take a pull request out of the merge queue',
    detail: `pl ci dequeue <pipeline> <pr>

  A queue run testing it is cancelled and the rest of its batch queued again.`,
    async run(ctx) {
      const number = parsePull(ctx.args.operand(1)!);
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const entry = await call(
        ciQueueDequeue({ client: ctx.api, path: { pipelineId: id, pull: number } }),
      );
      ctx.out.emit(entry, () =>
        ctx.out.line(`${label} #${entry.pull}  ${queueState(entry)}`),
      );
    },
  },
  {
    noun: 'ci',
    verb: 'queue',
    operands: 1,
    summary: 'Show the merge queue, then what merged or left it in the last day',
    detail: `pl ci queue <pipeline>

  The entries still to merge, in order: queued (with their place) or testing, the queue runs
  that tested each, and how it entered (api with who asked, or auto-merge). Then the entries
  merged or removed in the last day, newest first, with why a removed one left.`,
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const queue = await call(ciQueueGet({ client: ctx.api, path: { pipelineId: id } }));
      ctx.out.emit(queue, () => {
        if (queue.entries.length === 0) ctx.out.line('(the queue is empty)');
        else ctx.out.lines(queueRows(queue.entries, false));
        if (queue.finished.length === 0) return;
        ctx.out.line();
        ctx.out.line('finished in the last day:');
        ctx.out.lines(queueRows(queue.finished, true));
      });
    },
  },
  {
    noun: 'ci',
    verb: 'reliability',
    operands: 1,
    options: ['runs'],
    summary: 'Which CI steps are flaky, and which catch real problems',
    detail: `pl ci reliability <pipeline> [--runs <n>]

  Over the latest CI runs (default 200, at most 1000). Each failure is judged by the same
  step (or matrix cell) on the same tree with the same code. Steps are ranked by how flaky
  they are, never by how often they fail: a step that fails because it catches real problems
  is doing its job.

  OWN, INFRA, TIMEOUT  Attempts that failed: the step reported it, the platform around it
                       (pod, image pull, launch, storage), or it ran out of time
  FLAKY                Failed, and passed on the same tree and code: a false positive
  REPRODUCED           Failed again on the same tree and code, and never passed
  CAUGHT               Not run again on that tree, but a later head of the same pull
                       request passed: the code changed and the step went green
  UNVERIFIED           None of the above

  A flaky failure shows only when the same tree runs the step again (a rerun, a push back
  to an earlier tree, a queue run), so FLAKY is a lower bound. Reused steps aren't counted.`,
    async run(ctx) {
      const runs = ctx.args.option('runs');
      if (runs !== undefined && !/^\d+$/.test(runs))
        throw new CliError('--runs must be a number.');
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const report = await call(
        ciRunsReliability({
          client: ctx.api,
          path: { pipelineId: id },
          query: { runs: runs === undefined ? undefined : Number(runs) },
        }),
      );
      ctx.out.emit(report, () => printReliability(ctx.out, label, report));
    },
  },
  {
    noun: 'ci',
    verb: 'logs',
    operands: 1,
    flags: ['follow'],
    aliases: { f: 'follow' },
    summary: "Print a CI step's logs (latest attempt, or @n; --follow streams)",
    detail: `pl ci logs <pipeline>#ci<run>/<step>[@<attempt>] [--follow]

  Without @attempt: the latest attempt, whatever its status. A running attempt shows the
  pod's current logs. --follow/-f streams the log until the attempt finishes. A reused
  step's log is that of the attempt it reuses.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!, false, 'ci');
      if (ctx.args.flag('follow')) return followLog(ctx, addr);
      const log = await call(
        ciRunStepsLogs({
          client: ctx.api,
          path: {
            pipelineId: addr.id,
            number: addr.address.run,
            step: addr.address.step,
          },
          query: { attempt: addr.address.attempt },
        }),
      );
      ctx.out.write(
        log.text.length === 0 || log.text.endsWith('\n') ? log.text : log.text + '\n',
      );
    },
  },
  {
    noun: 'ci',
    verb: 'output',
    operands: 1,
    summary: "Print a CI step's output (latest successful attempt, or @n)",
    detail: `pl ci output <pipeline>#ci<run>/<step>[@<attempt>]

  Prints the output JSON on stdout. Without @attempt: the latest successful attempt. --json
  prints the full response (run, step, attempt, status, output, outputKey).`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!, false, 'ci');
      const output = await call(
        ciRunStepsOutput({
          client: ctx.api,
          path: {
            pipelineId: addr.id,
            number: addr.address.run,
            step: addr.address.step,
          },
          query: { attempt: addr.address.attempt },
        }),
      );
      ctx.out.emit(output, () => {
        ctx.log(
          `${formatCiAddress(addr.label, output.run, output.step, output.attempt)} (${output.status})`,
        );
        ctx.out.line(output.output == null ? 'null' : indented(output.output));
      });
    },
  },
  {
    noun: 'ci',
    verb: 'files',
    operands: 1,
    summary: "List a CI step's stored output files",
    detail: `pl ci files <pipeline>#ci<run>/<step>[@<attempt>]

  Lists the files the attempt stored. Download one with \`pl ci file <address> <path>\`.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!, false, 'ci');
      const files = await call(
        ciRunStepsFiles({
          client: ctx.api,
          path: {
            pipelineId: addr.id,
            number: addr.address.run,
            step: addr.address.step,
          },
          query: { attempt: addr.address.attempt },
        }),
      );
      ctx.out.emit(files, () => {
        ctx.out.line(formatCiAddress(addr.label, files.run, files.step, files.attempt));
        if (files.files.length === 0) {
          ctx.out.line('(no files)');
          return;
        }
        ctx.out.lines(
          table(
            ['PATH', 'SIZE', 'MODIFIED'],
            files.files.map((f) => [f.path, size(f.size), time(f.lastModified)]),
          ),
        );
      });
    },
  },
  {
    noun: 'ci',
    verb: 'file',
    operands: 1,
    options: ['output'],
    aliases: { o: 'output' },
    summary: "Download one of a CI step's files (to stdout, or -o <file>)",
    detail: `pl ci file <pipeline>#ci<run>/<step>[@<attempt>] <path> [-o <file>]
pl ci file <pipeline>#ci<run>/<step>[@<attempt>]/<path> [-o <file>]

  Writes the file to stdout, or to <file> with -o/--output. A directory ref \`dir/\`
  downloads as a tar.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!, true, 'ci');
      const path = ctx.args.operand(1) ?? addr.address.path;
      if (path === undefined)
        throw new CliError(
          'Which file? Pass a path: `pl ci file <address> <path>` (list them with `pl ci files`).',
        );
      if (ctx.args.operand(1) !== undefined && addr.address.path !== undefined)
        throw new CliError(
          'Give the file path once: in the address or as an argument, not both.',
        );
      const outFile = ctx.args.option('output');
      const response = await downloadStepFile(
        ctx.api,
        {
          pipelineId: addr.id,
          number: addr.address.run,
          step: addr.address.step,
          path,
          attempt: addr.address.attempt,
        },
        ctx.signal,
        'ci',
      );
      if (outFile === undefined || outFile === '-') {
        if (response.body)
          for await (const chunk of response.body) ctx.stdoutBytes(chunk);
        return;
      }
      const full = resolve(outFile);
      const temp = `${full}.part`;
      try {
        const bytes = await Bun.write(temp, response);
        renameSync(temp, full);
        if (!ctx.json) ctx.out.line(`wrote ${full} (${size(bytes)})`);
      } catch (error) {
        rmSync(temp, { force: true });
        throw new CliError(`Could not write '${full}': ${describe(error)}`);
      }
    },
  },
];

const percent = (rate: number) => `${(rate * 100).toFixed(1)}%`;

export function printReliability(
  out: Output,
  pipeline: string,
  report: CiReliabilityReport,
): void {
  out.line(
    report.runs === 0
      ? 'no CI runs yet'
      : `CI runs #ci${report.fromRun}–#ci${report.toRun} (${report.runs} of the latest ${report.requestedRuns} asked for)`,
  );
  if (report.steps.length) {
    out.line();
    out.lines(
      table(
        [
          'STEP',
          'ATTEMPTS',
          'OWN',
          'INFRA',
          'TIMEOUT',
          'FLAKY',
          'RATE',
          'REPRODUCED',
          'CAUGHT',
          'UNVERIFIED',
        ],
        report.steps.map((s) => [
          s.step,
          String(s.attempts),
          String(s.ownFailures),
          String(s.infrastructureFailures),
          String(s.timeouts),
          String(s.flakyFailures),
          percent(s.flakyRate),
          String(s.reproducedFailures),
          String(s.caughtFailures),
          String(s.unverifiedFailures),
        ]),
      ),
    );
  }
  const flaky = report.steps.filter((s) => s.flaky.length);
  if (flaky.length) {
    out.line();
    out.line('flaky failures (failed, then passed on the same tree and code):');
    for (const s of flaky)
      for (const f of s.flaky)
        out.line(
          `  ${formatCiAddress(pipeline, f.failed.run, f.failed.step, f.failed.attempt)}  passed in ${formatCiAddress(pipeline, f.passed.run, f.passed.step, f.passed.attempt)}  ${f.pulls.map((n) => `#${n}`).join(' ')}  ${time(f.failedAt)}`,
        );
  }
  for (const note of report.notes) out.line(note);
}
