import { CliError, ExitCode, describe } from './errors';
import { readChanges } from './events';
import type { CommandContext } from './registry';

export type WaitCheck =
  | { kind: 'pending'; progress: string }
  | { kind: 'done'; output: string; success: boolean }
  | { kind: 'needsInput'; output: string }
  | { kind: 'held'; output: string };

export const pending = (progress: string): WaitCheck => ({ kind: 'pending', progress });
export const done = (output: string, success: boolean): WaitCheck => ({
  kind: 'done',
  output,
  success,
});
export const needsInput = (output: string): WaitCheck => ({ kind: 'needsInput', output });
export const held = (output: string): WaitCheck => ({ kind: 'held', output });

/** Tunables, exported so tests can shorten the pauses. */
export const waitSettings = {
  /** Re-check at least this often, in case the event stream stalls. */
  fallbackMs: 60_000,
  /** After the server ends the stream (a redeploy), before reconnecting. */
  reconnectMs: 2_000,
  /** After the stream fails. */
  errorMs: 5_000,
  /** After a retryable check failure: 2s, 4s, … capped at 30s. */
  backoffMs: (failures: number) => Math.min(30, 2 ** Math.min(failures, 5)) * 1000,
};

/** `90s`, `30m`, `2h` (seconds without a unit) as milliseconds. */
export function parseTimeout(text: string | undefined): number | undefined {
  if (!text?.trim()) return undefined;
  const match = /^(\d+(?:\.\d+)?)([smh]?)$/.exec(text.trim());
  const n = match ? Number(match[1]) : NaN;
  if (!match || !(n > 0))
    throw new CliError(`Invalid --timeout '${text}' (e.g. 90s, 30m, 2h).`);
  return n * { '': 1000, s: 1000, m: 60_000, h: 3_600_000 }[match[2]!]!;
}

const clock = () => {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
};

/**
 * Runs `check` until it is done: again on each of the pipeline's `wakeOn` events (and `resync`),
 * every {@link waitSettings.fallbackMs} regardless, and after a backoff when it failed with a
 * retryable problem. Progress goes to stderr when it changes; the outcome's line to stdout, with
 * the exit code on the context (1 for a failed outcome, 3 when a person must answer, 4 when the
 * run is held, 124 on `--timeout`). Ctrl+C ends it with a cancelled error.
 */
export async function waitFor(
  ctx: CommandContext,
  pipelineId: string,
  wakeOn: ReadonlySet<string>,
  check: (signal: AbortSignal) => Promise<WaitCheck>,
): Promise<void> {
  const timeoutMs = parseTimeout(ctx.args.option('timeout'));
  const deadline = new AbortController();
  const onAbort = () => deadline.abort();
  ctx.signal.addEventListener('abort', onAbort, { once: true });
  if (ctx.signal.aborted) deadline.abort();
  let timedOut = false;
  const timer =
    timeoutMs === undefined
      ? undefined
      : setTimeout(() => {
          timedOut = true;
          deadline.abort();
        }, timeoutMs);

  let wake: (() => void) | undefined;
  let woken = false;
  const nudge = () => {
    woken = true;
    wake?.();
  };
  const watch = watchEvents(ctx, pipelineId, wakeOn, nudge, deadline.signal);

  let lastProgress: string | undefined;
  let failures = 0;
  try {
    for (;;) {
      if (deadline.signal.aborted) break;
      let waitMs = waitSettings.fallbackMs;
      let state: WaitCheck;
      try {
        state = await check(deadline.signal);
        failures = 0;
      } catch (error) {
        if (deadline.signal.aborted) break;
        if (!(error instanceof CliError) || !error.retryable) throw error;
        waitMs = waitSettings.backoffMs(++failures);
        state = pending(`server unavailable (${error.problems[0]}); retrying`);
      }
      switch (state.kind) {
        case 'done':
          ctx.out.line(state.output);
          if (!state.success) ctx.exitCode = ExitCode.Failure;
          return;
        case 'needsInput':
          ctx.out.line(state.output);
          ctx.exitCode = ExitCode.NeedsInput;
          return;
        case 'held':
          ctx.out.line(state.output);
          ctx.exitCode = ExitCode.Held;
          return;
        case 'pending':
          if (state.progress !== lastProgress) {
            ctx.stderr(`${clock()}  ${state.progress}`);
            lastProgress = state.progress;
          }
      }
      if (!woken) {
        await new Promise<void>((resolve) => {
          wake = resolve;
          const stop = () => resolve();
          deadline.signal.addEventListener('abort', stop, { once: true });
          ctx.sleep(waitMs).then(resolve, resolve);
        });
      }
      woken = false;
      wake = undefined;
    }
    if (timedOut) {
      ctx.stderr(
        `Timed out after ${ctx.args.option('timeout')}${lastProgress ? ` (${lastProgress})` : ''}.`,
      );
      ctx.exitCode = ExitCode.Timeout;
      return;
    }
    throw new CliError('Cancelled.');
  } finally {
    clearTimeout(timer);
    deadline.abort();
    ctx.signal.removeEventListener('abort', onAbort);
    await watch;
  }
}

/** Keeps a stream of the pipeline's events open, nudging on the interesting ones and on every reconnect. */
async function watchEvents(
  ctx: CommandContext,
  pipelineId: string,
  wakeOn: ReadonlySet<string>,
  nudge: () => void,
  signal: AbortSignal,
): Promise<void> {
  while (!signal.aborted) {
    try {
      await readChanges(
        ctx,
        pipelineId,
        (e) => {
          if (e.type === 'resync' || wakeOn.has(e.type)) nudge();
        },
        signal,
      );
      if (signal.aborted) return;
      nudge(); // reconnected: something may have changed meanwhile
      await ctx.sleep(waitSettings.reconnectMs);
    } catch (error) {
      if (signal.aborted) return;
      ctx.log(`event stream: ${describe(error)}`);
      await ctx.sleep(waitSettings.errorMs);
    }
  }
}
