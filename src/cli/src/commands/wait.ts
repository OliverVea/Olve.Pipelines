import {
  bindingsStatus,
  runsGet,
  runsList,
  type Run,
  type RunStep,
  type RunSummary,
} from '@olve/api';
import { formatAddress, parseAddress, resolvePipeline } from '../address';
import { call } from '../api';
import { CliError } from '../errors';
import { short } from '../format';
import type { Command, CommandContext } from '../registry';
import { done, needsInput, pending, waitFor, type WaitCheck } from '../wait';
import { describePending } from './pipeline';

const clock = () => {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
};

const isTerminal = (status: string) =>
  ['Succeeded', 'Failed', 'Cancelled', 'Superseded'].includes(status);

/** A step only a person can move on (a callback or poll answers by itself). */
const needsPerson = (step: RunStep) =>
  step.status === 'waiting' && (!step.waitingFor || step.waitingFor.includes('person'));

/** The run `cursor` points at, following `supersededBy` when `follow`: the newer run contains the commit. */
async function getRunFollowing(
  ctx: CommandContext,
  pipelineId: string,
  label: string,
  cursor: { value: number },
  follow: boolean,
): Promise<Run> {
  for (;;) {
    const run = await call(
      runsGet({ client: ctx.api, path: { pipelineId, number: cursor.value } }),
    );
    if (!follow || run.status !== 'Superseded' || run.supersededByNumber == null)
      return run;
    ctx.stderr(
      `${clock()}  ${formatAddress(label, cursor.value)} superseded by #${run.supersededByNumber}, following`,
    );
    cursor.value = run.supersededByNumber;
  }
}

const runFinishedSummary = (label: string, run: RunSummary): WaitCheck =>
  isTerminal(run.status)
    ? done(
        `${formatAddress(label, run.number)}  ${run.status}`,
        run.status === 'Succeeded',
      )
    : pending(`${formatAddress(label, run.number)}: ${run.status}`);

/** Like the summary's, but a run that waits for a person is reported as such. */
export function runFinished(label: string, run: Run): WaitCheck {
  const waiting = isTerminal(run.status)
    ? undefined
    : run.stages.flatMap((s) => s.steps).find(needsPerson);
  return waiting
    ? needsInput(`${formatAddress(label, run.number)}/${waiting.name}  waiting for input`)
    : runFinishedSummary(label, run);
}

function runState(label: string, run: Run, until: string): WaitCheck {
  const address = formatAddress(label, run.number);
  switch (until) {
    case 'started':
      return done(`${address}  ${run.status}`, true);
    case 'held':
      if (run.heldAt) return done(`${address}  held at ${run.heldAt}`, true);
      if (isTerminal(run.status))
        return done(`${address}  ${run.status} (never held)`, false);
      return pending(`${address}: ${run.status}, not held`);
    default:
      return runFinished(label, run);
  }
}

function stepState(label: string, run: Run, step: string, until: string): WaitCheck {
  const view = run.stages.flatMap((s) => s.steps).find((s) => s.name === step);
  if (!view) throw new CliError(`Run #${run.number} has no step '${step}'.`);
  const name = `${formatAddress(label, run.number)}/${step}`;
  const line = `${name}  ${view.status}${view.reason ? ` (${view.reason})` : ''}`;
  const settled = [
    'succeeded',
    'skipped',
    'excluded',
    'failed',
    'cancelled',
    'obsolete',
  ].includes(view.status);
  const runEnded = () =>
    isTerminal(run.status) ? done(`${line} (run ${run.status})`, false) : undefined;
  if (needsPerson(view) && until !== 'started')
    return needsInput(`${name}  waiting for input`);
  if (until === 'started')
    return view.attemptCount > 0 || settled
      ? done(line, view.status !== 'failed' && view.status !== 'cancelled')
      : (runEnded() ?? pending(`${name}: ${view.status}`));
  if (until === 'held')
    return view.status === 'held'
      ? done(line, true)
      : settled
        ? done(`${line} (never held)`, false)
        : (runEnded() ?? pending(`${name}: ${view.status}`));
  if (settled)
    return done(line, ['succeeded', 'skipped', 'excluded'].includes(view.status));
  return runEnded() ?? pending(`${name}: ${view.status}`);
}

const inputSha = (run: Run) => (typeof run.input.sha === 'string' ? run.input.sha : '');

export const waitCommands: Command[] = [
  {
    noun: 'wait',
    verb: 'run',
    operands: 1,
    options: ['until', 'timeout'],
    flags: ['no-follow'],
    summary:
      'Wait for a run or step to finish (or --until started/held); exit 0 on success',
    detail: `pl wait run <pipeline>#<run>[/<step>] [--until finished|started|held] [--timeout 45m] [--no-follow]

  Blocks until the run (or the step) reaches the state, then prints it. Re-checks on the
  pipeline's live events, and every minute in case the stream stalls. A superseded run is
  followed to the run that superseded it (printed on stderr); the result is that run's.

  --until finished  (default) run: Succeeded → exit 0; Failed or Cancelled → 1.
                    step: succeeded, skipped or excluded → 0; failed or cancelled, or the run
                    ended without running it → 1. A step waiting for a person → 3 (answer it
                    with pl input answer, or decline it).
  --until started   the step has an attempt (or the run exists).
  --until held      the run is held at a blocked step; exits 1 if it finishes first.
  --timeout <dur>   give up after 90s / 30m / 2h: exit 124.
  --no-follow       stop at a superseded run instead: exit 1.

  Progress goes to stderr, the result to stdout.`,
    async run(ctx) {
      const address = parseAddress(ctx.args.operand(0)!);
      const { id } = await resolvePipeline(ctx.api, address.pipeline);
      const until = ctx.args.optionOr('until', 'finished');
      if (!['finished', 'started', 'held'].includes(until))
        throw new CliError(`--until must be finished, started or held, got '${until}'.`);
      const cursor = { value: address.run };
      const follow = !ctx.args.flag('no-follow');
      await waitFor(ctx, id, new Set(['run', 'attempt']), async () => {
        const run = await getRunFollowing(ctx, id, address.pipeline, cursor, follow);
        return address.step !== undefined
          ? stepState(address.pipeline, run, address.step, until)
          : until === 'finished'
            ? runFinished(address.pipeline, run)
            : runState(address.pipeline, run, until);
      });
    },
  },
  {
    noun: 'wait',
    verb: 'push',
    operands: 2,
    options: ['until', 'timeout'],
    flags: ['no-follow'],
    summary:
      'Wait for a run of a commit to start (or --until finished); prints its address',
    detail: `pl wait push <pipeline> <sha> [--until started|finished] [--timeout 30m] [--no-follow]

  Blocks until a run whose input sha is <sha> (a prefix is enough) exists, and prints its
  address (e.g. olve-pipelines#412): addr=$(pl wait push olve-pipelines 6881313).
  --until finished keeps waiting until that run finishes or waits for input (exit as
  \`pl wait run\`). When a
  newer push supersedes it, the wait follows the newer run, which contains the commit;
  --no-follow stops there instead (exit 1).
  Exits 1 when the binding's reconcile of that commit failed (no run will start).`,
    async run(ctx) {
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const sha = ctx.args.operand(1)!.toLowerCase();
      const until = ctx.args.optionOr('until', 'started');
      if (!['started', 'finished'].includes(until))
        throw new CliError(`--until must be started or finished, got '${until}'.`);
      const notIt = new Set<number>();
      let found: { value: number } | undefined;
      const follow = !ctx.args.flag('no-follow');
      await waitFor(ctx, id, new Set(['run', 'attempt', 'binding']), async () => {
        if (found)
          return runFinished(label, await getRunFollowing(ctx, id, label, found, follow));
        const page = await call(
          runsList({
            client: ctx.api,
            path: { pipelineId: id },
            query: { offset: 0, limit: 15 },
          }),
        );
        for (const summary of page.items.filter((r) => !notIt.has(r.number))) {
          let run = await call(
            runsGet({
              client: ctx.api,
              path: { pipelineId: id, number: summary.number },
            }),
          );
          if (!inputSha(run).toLowerCase().startsWith(sha)) {
            notIt.add(summary.number);
            continue;
          }
          if (until === 'started')
            return done(formatAddress(label, summary.number), true);
          found = { value: summary.number };
          ctx.stderr(
            `${clock()}  ${formatAddress(label, summary.number)} is the run of ${sha}`,
          );
          if (follow && run.status === 'Superseded')
            run = await getRunFollowing(ctx, id, label, found, follow);
          return runFinished(label, run);
        }
        let binding;
        try {
          binding = await call(
            bindingsStatus({ client: ctx.api, path: { pipelineId: id } }),
          );
        } catch {
          return pending(`no run of ${sha} yet`);
        }
        if (
          binding.result === 'Error' &&
          binding.reconcileSha?.toLowerCase().startsWith(sha)
        )
          return done(
            `reconcile of ${sha} failed: ${binding.problems.join('; ')}`,
            false,
          );
        return pending(
          binding.reconcileInProgress
            ? `no run of ${sha} yet (reconciling)`
            : `no run of ${sha} yet`,
        );
      });
    },
  },
  {
    noun: 'wait',
    verb: 'reconcile',
    operands: 2,
    options: ['timeout'],
    summary:
      'Wait for the binding to apply a config commit; exit 1 if its reconcile fails',
    detail: `pl wait reconcile <pipeline> <sha> [--timeout 20m]

  Blocks until the binding has applied config commit <sha> (a prefix is enough): prints the
  version it switched to. Exits 1 when that commit's reconcile fails. <sha> must touch the
  binding path (or its local dependencies); other commits are never reconciled.`,
    async run(ctx) {
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const sha = ctx.args.operand(1)!.toLowerCase();
      await waitFor(ctx, id, new Set(['binding', 'version']), async () => {
        const b = await call(
          bindingsStatus({ client: ctx.api, path: { pipelineId: id } }),
        );
        const is = (s: string | null | undefined) =>
          !!s && s.toLowerCase().startsWith(sha);
        if (is(b.lastReconciledSha) && b.result === 'Success')
          return done(
            b.pending && !b.pending.held && b.pending.version === b.reconciledVersion
              ? `${label}: ${short(b.lastReconciledSha)} applied; ${describePending(b.pending)}`
              : `${label}: ${short(b.lastReconciledSha)} applied (v${b.reconciledVersion})`,
            true,
          );
        if (is(b.reconcileSha) && b.result === 'Error' && !b.reconcileInProgress)
          return done(
            `${label}: reconcile of ${short(b.reconcileSha)} failed: ${b.problems.join('; ')}`,
            false,
          );
        return pending(
          b.reconcileInProgress
            ? `${label}: reconciling ${short(b.reconcileSha)}`
            : `${label}: at ${short(b.lastReconciledSha)}, waiting for ${sha}`,
        );
      });
    },
  },
];
