import type { ChangeEvent } from '@olve/api';
import { resolvePipeline } from '../address';
import { readChanges } from '../events';
import type { Command } from '../registry';

function describeEvent(event: ChangeEvent, omitPipeline: boolean): string {
  if (event.type === 'resync') return 'missed events: refetch what you are looking at';
  return Object.entries(event)
    .filter(([name]) => name !== 'type' && !(omitPipeline && name === 'pipelineId'))
    .map(
      ([name, value]) =>
        `${name}=${typeof value === 'string' ? value : JSON.stringify(value)}`,
    )
    .join(' ');
}

const clock = () => {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
};

export const watchCommand: Command = {
  noun: 'watch',
  summary:
    'Print live change events (runs, attempts, versions, blocked steps, bindings) until Ctrl+C',
  detail: `pl watch [<pipeline>] [--json]

  Streams the instance's change notifications (all pipelines, or one). Each line is
  \`<time> <event> <fields>\`; --json prints {"event": ..., "data": ...} per line.`,
  async run(ctx) {
    const text = ctx.args.operand(0);
    const pipelineId =
      text === undefined ? undefined : (await resolvePipeline(ctx.api, text)).id;
    await readChanges(
      ctx,
      pipelineId,
      (e) => {
        if (e.type === 'ping' || e.type === 'hello') return;
        ctx.out.line(
          ctx.json
            ? JSON.stringify({ event: e.type, data: e })
            : `${clock()}  ${e.type.padEnd(8)}  ${describeEvent(e, pipelineId !== undefined)}`,
        );
      },
      ctx.signal,
    );
    if (!ctx.signal.aborted)
      ctx.stderr('The event stream ended (the service restarted?).');
  },
};
