import { renameSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  runsGet,
  runStepsAttempts,
  runStepsCancel,
  runStepsFiles,
  runStepsLogs,
  runStepsOutput,
  runStepsRerun,
  stepsBlock,
  stepsBlocked,
  stepsHistory,
  stepsUnblock,
  type Attempt,
} from '@olve/api';
import {
  formatAddress,
  parseStepRef,
  resolvePipeline,
  resolveStep,
  type ResolvedAddress,
} from '../address';
import { apiError, call } from '../api';
import { CliError, describe } from '../errors';
import { duration, indented, time } from '../format';
import { table } from '../output';
import type { Command, CommandContext } from '../registry';

export const isActive = (a: Attempt) =>
  a.status.type === 'scheduled' || a.status.type === 'in-progress';

export function size(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GiB`;
}

const attemptLine = (label: string, a: Attempt) =>
  `${formatAddress(label, a.runNumber, a.stepName, a.number)}  ${a.status.type}`;

export const printBlocked = (ctx: CommandContext, blocked: string[]) =>
  ctx.out.line(
    blocked.length === 0 ? 'no blocked steps' : `blocked: ${blocked.join(', ')}`,
  );

async function toggleBlock(ctx: CommandContext, block: boolean): Promise<void> {
  const ref = parseStepRef(ctx.args.operand(0)!);
  const { id } = await resolvePipeline(ctx.api, ref.pipeline);
  const blocked = await call(
    (block ? stepsBlock : stepsUnblock)({
      client: ctx.api,
      path: { pipelineId: id, step: ref.step },
    }),
  );
  ctx.out.emit(blocked, () => {
    ctx.out.line(`${block ? 'blocked' : 'unblocked'} ${ref.pipeline}/${ref.step}`);
    printBlocked(ctx, blocked);
  });
}

/** The file route is outside the contract (a binary body): a raw request with the bearer. */
async function downloadFile(
  ctx: CommandContext,
  addr: ResolvedAddress & { address: { step: string } },
  path: string,
): Promise<Response> {
  const query =
    addr.address.attempt !== undefined ? `?attempt=${addr.address.attempt}` : '';
  const route = `/api/pipelines/${addr.id}/runs/${addr.address.run}/steps/${encodeURIComponent(addr.address.step)}/files/${path.split('/').map(encodeURIComponent).join('/')}${query}`;
  const headers: Record<string, string> = {};
  const token = await ctx.token();
  if (token) headers.Authorization = `Bearer ${token}`;
  ctx.log(`GET ${route}`);
  let response: Response;
  try {
    response = await ctx.fetch(`${ctx.api.getConfig().baseUrl}${route}`, {
      headers,
      signal: ctx.signal,
    });
  } catch (error) {
    throw new CliError(`Request to ${route} failed: ${describe(error)}`, true);
  }
  if (!response.ok) {
    const text = await response.text().catch(() => '');
    let body: unknown = text;
    try {
      body = text ? JSON.parse(text) : '';
    } catch {
      body = text;
    }
    throw apiError(response.status, response.statusText, body);
  }
  return response;
}

export const stepCommands: Command[] = [
  {
    noun: 'step',
    verb: 'output',
    operands: 1,
    summary: "Print a step's output (latest successful attempt, or @n)",
    detail: `pl step output <pipeline>#<run>/<step>[@<attempt>]

  Prints the output JSON on stdout (pipe it to jq). Without @attempt: the latest successful
  attempt — the one later stages consumed. --json prints the full response (run, step,
  attempt, status, output, outputKey).`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!);
      const output = await call(
        runStepsOutput({
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
          `${formatAddress(addr.label, output.run, output.step, output.attempt)} (${output.status})`,
        );
        ctx.out.line(output.output == null ? 'null' : indented(output.output));
      });
    },
  },
  {
    noun: 'step',
    verb: 'files',
    operands: 1,
    summary: "List a step's stored output files",
    detail: `pl step files <pipeline>#<run>/<step>[@<attempt>]

  Lists the files the attempt stored (a directory ref \`dir/\` is stored as dir.tar).
  Download one with \`pl step file <address> <path>\`.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!);
      const files = await call(
        runStepsFiles({
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
        ctx.out.line(formatAddress(addr.label, files.run, files.step, files.attempt));
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
    noun: 'step',
    verb: 'file',
    operands: 1,
    options: ['output'],
    aliases: { o: 'output' },
    summary: "Download one of a step's files (to stdout, or -o <file>)",
    detail: `pl step file <pipeline>#<run>/<step>[@<attempt>] <path> [-o <file>]
pl step file <pipeline>#<run>/<step>[@<attempt>]/<path> [-o <file>]

  Writes the file to stdout, or to <file> with -o/--output. A directory ref \`dir/\`
  downloads as a tar.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!, true);
      const path = ctx.args.operand(1) ?? addr.address.path;
      if (path === undefined)
        throw new CliError(
          'Which file? Pass a path: `pl step file <address> <path>` (list them with `pl step files`).',
        );
      if (ctx.args.operand(1) !== undefined && addr.address.path !== undefined)
        throw new CliError(
          'Give the file path once: in the address or as an argument, not both.',
        );

      const outFile = ctx.args.option('output');
      const response = await downloadFile(ctx, addr, path);
      if (outFile === undefined || outFile === '-') {
        if (response.body)
          for await (const chunk of response.body) ctx.stdoutBytes(chunk);
        return;
      }
      // Download to a temp sibling and move into place, so a failed download never leaves a partial file.
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
  {
    noun: 'step',
    verb: 'logs',
    operands: 1,
    options: ['interval'],
    flags: ['follow'],
    aliases: { f: 'follow' },
    summary: "Print a step attempt's logs (latest attempt, or @n; --follow polls)",
    detail: `pl step logs <pipeline>#<run>/<step>[@<attempt>] [--follow] [--interval <seconds>]

  Without @attempt: the latest attempt, whatever its status. A running attempt shows the
  pod's current logs. --follow/-f polls until the attempt finishes (default every 2s).`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!);
      const path = {
        pipelineId: addr.id,
        number: addr.address.run,
        step: addr.address.step,
      };
      if (!ctx.args.flag('follow')) {
        const log = await call(
          runStepsLogs({
            client: ctx.api,
            path,
            query: { attempt: addr.address.attempt },
          }),
        );
        ctx.out.write(
          log.text.length === 0 || log.text.endsWith('\n') ? log.text : log.text + '\n',
        );
        return;
      }
      const seconds = Number(ctx.args.optionOr('interval', '2'));
      if (!(seconds > 0))
        throw new CliError('--interval must be a positive number of seconds.');

      let attempt: number | undefined;
      let printed = '';
      for (;;) {
        if (attempt === undefined) {
          const attempts = await call(runStepsAttempts({ client: ctx.api, path }));
          attempt = (
            addr.address.attempt !== undefined
              ? attempts.find((a) => a.number === addr.address.attempt)
              : attempts.at(-1)
          )?.number;
        }
        if (attempt === undefined) {
          // No attempt yet: keep waiting only while the run can still reach the step.
          const run = await call(
            runsGet({
              client: ctx.api,
              path: { pipelineId: addr.id, number: addr.address.run },
            }),
          );
          if (run.status !== 'Running')
            throw new CliError(
              `Run #${addr.address.run} is ${run.status} and has no ${addr.address.attempt !== undefined ? `attempt ${addr.address.attempt}` : 'attempt'} of step '${addr.address.step}'.`,
            );
        } else {
          const log = await call(
            runStepsLogs({ client: ctx.api, path, query: { attempt } }),
          );
          if (log.text.startsWith(printed)) ctx.out.write(log.text.slice(printed.length));
          // The source changed (init container -> step container, or the stored log replaced the live one).
          else
            ctx.out.write(
              (printed.length > 0 && !printed.endsWith('\n') ? '\n' : '') + log.text,
            );
          printed = log.text;
          if (log.complete) {
            if (printed.length > 0 && !printed.endsWith('\n')) ctx.out.write('\n');
            ctx.log(`attempt @${log.attempt} ${log.status}`);
            return;
          }
        }
        await ctx.sleep(seconds * 1000);
        if (ctx.signal.aborted) return;
      }
    },
  },
  {
    noun: 'step',
    verb: 'rerun',
    operands: 1,
    flags: ['continue'],
    summary: 'Rerun one step as a new attempt (--continue re-opens the run downstream)',
    detail: `pl step rerun <pipeline>#<run>/<step> [--continue]

  Adds an attempt in the same run (same pinned version, current pool input). Rerun only by
  default; --continue also continues the run downstream of the step.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!);
      if (addr.address.attempt !== undefined)
        throw new CliError(
          `A rerun creates a new attempt; drop the @${addr.address.attempt} from the address.`,
        );
      const cont = ctx.args.flag('continue');
      const attempt = await call(
        runStepsRerun({
          client: ctx.api,
          path: {
            pipelineId: addr.id,
            number: addr.address.run,
            step: addr.address.step,
          },
          body: { continue: cont },
        }),
      );
      ctx.out.emit(attempt, () =>
        ctx.out.line(
          `${attemptLine(addr.label, attempt)}${cont ? ' (run continues downstream)' : ''}`,
        ),
      );
    },
  },
  {
    noun: 'step',
    verb: 'cancel',
    operands: 1,
    summary: "Cancel a step's scheduled or in-progress attempt",
    detail: `pl step cancel <pipeline>#<run>/<step>

  Cancels the step's scheduled or in-progress attempt (its K8s Job is deleted). Like any
  cancelled attempt, the other steps of its stage keep running and the run ends Cancelled
  once the stage settles. \`pl step rerun <address> --continue\` re-opens the run.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!);
      if (addr.address.attempt !== undefined)
        throw new CliError(
          `Cancel targets the step's active attempt; drop the @${addr.address.attempt} from the address.`,
        );
      const attempt = await call(
        runStepsCancel({
          client: ctx.api,
          path: {
            pipelineId: addr.id,
            number: addr.address.run,
            step: addr.address.step,
          },
        }),
      );
      ctx.out.emit(attempt, () => ctx.out.line(attemptLine(addr.label, attempt)));
    },
  },
  {
    noun: 'step',
    verb: 'history',
    operands: 1,
    options: ['limit'],
    summary: "A step's history across runs, with definition-change markers",
    detail: `pl step history <pipeline>/<step> [--limit <n>]

  Newest first. A \`*\` marks the first row after the step's definition changed (its step
  version differs from the row below).`,
    async run(ctx) {
      let limit: number | undefined;
      const limitText = ctx.args.option('limit');
      if (limitText !== undefined) {
        limit = Number(limitText);
        if (!/^\d+$/.test(limitText) || limit < 1)
          throw new CliError('--limit must be a positive integer.');
      }
      const ref = parseStepRef(ctx.args.operand(0)!);
      const { id } = await resolvePipeline(ctx.api, ref.pipeline);
      const items = await call(
        stepsHistory({
          client: ctx.api,
          path: { pipelineId: id, step: ref.step },
          query: { limit },
        }),
      );
      ctx.out.emit(items, () => {
        if (items.length === 0) {
          ctx.out.line(`(no attempts of '${ref.step}')`);
          return;
        }
        ctx.out.lines(
          table(
            [
              '  ATTEMPT',
              'STATUS',
              'TOOK',
              'STEP VERSION',
              'PIPELINE',
              'CREATED',
              'REASON',
            ],
            items.map((i) => [
              `${i.stepVersionChanged ? '*' : ' '} #${i.runNumber}@${i.attemptNumber}`,
              i.status,
              duration(i.startedAt, i.finishedAt),
              i.stepVersion != null ? `${ref.step}.${i.stepVersion}` : '-',
              `v${i.versionNumber}`,
              time(i.createdAt),
              i.reason ?? '',
            ]),
          ),
        );
      });
    },
  },
  {
    noun: 'step',
    verb: 'blocked',
    operands: 1,
    summary: "List a pipeline's blocked steps",
    detail: `pl step blocked <pipeline>

  Blocks are keyed on step name and survive version switches. A blocked step gets no
  attempt: a run reaching it is held there until the step is unblocked.`,
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const blocked = await call(
        stepsBlocked({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(blocked, () => printBlocked(ctx, blocked));
    },
  },
  {
    noun: 'step',
    verb: 'block',
    operands: 1,
    summary:
      'Block a step: runs reaching it are held there (while working on the pipeline)',
    detail: `pl step block <pipeline>/<step>

  Holds every run that reaches the step, however it started, until unblocked. Use it
  sparingly (while working on the pipeline or its target), not as an approval step.
  Prints the pipeline's blocked steps afterwards.`,
    run: (ctx) => toggleBlock(ctx, true),
  },
  {
    noun: 'step',
    verb: 'unblock',
    operands: 1,
    summary: 'Unblock a step: held runs resume',
    detail:
      "pl step unblock <pipeline>/<step>   Prints the pipeline's blocked steps afterwards.",
    run: (ctx) => toggleBlock(ctx, false),
  },
];
