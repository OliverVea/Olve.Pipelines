import { resolvePipeline } from '../address';
import { readEvents } from '../events';
import type { Command } from '../registry';

function describeEvent(type: string, data: string, omitPipeline: boolean): string {
  if (type === 'resync') return 'missed events: refetch what you are looking at';
  try {
    const parsed = JSON.parse(data) as Record<string, unknown>;
    return Object.entries(parsed)
      .filter(([name]) => name !== 'type' && !(omitPipeline && name === 'pipelineId'))
      .map(
        ([name, value]) =>
          `${name}=${typeof value === 'string' ? value : JSON.stringify(value)}`,
      )
      .join(' ');
  } catch {
    return data;
  }
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
    await readEvents(
      ctx,
      pipelineId === undefined
        ? '/api/events'
        : `/api/events?pipeline=${encodeURIComponent(pipelineId)}`,
      (e) => {
        if (e.event === 'ping' || e.event === 'hello') return;
        ctx.out.line(
          ctx.json
            ? `{"event":${JSON.stringify(e.event)},"data":${e.data.length === 0 ? 'null' : e.data}}`
            : `${clock()}  ${e.event.padEnd(8)}  ${describeEvent(e.event, e.data, pipelineId !== undefined)}`,
        );
      },
      ctx.signal,
    );
    if (!ctx.signal.aborted)
      ctx.stderr('The event stream ended (the service restarted?).');
  },
};
