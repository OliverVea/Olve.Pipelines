import { apiError } from './api';
import { CliError, describe } from './errors';
import type { CommandContext } from './registry';

export type SseEvent = { event: string; data: string; id?: string };

/**
 * Reads a server-sent event stream until the server ends it or `signal` aborts (then it returns
 * quietly). A non-2xx answer is a {@link CliError}; a lost connection is a retryable one.
 */
export async function readEvents(
  ctx: Pick<CommandContext, 'api' | 'token' | 'fetch' | 'log'>,
  path: string,
  onEvent: (event: SseEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  const url = `${ctx.api.getConfig().baseUrl}${path}`;
  const headers: Record<string, string> = { Accept: 'text/event-stream' };
  const token = await ctx.token();
  if (token) headers.Authorization = `Bearer ${token}`;
  ctx.log(`GET ${path} (events)`);

  let response: Response;
  try {
    response = await ctx.fetch(url, { headers, signal });
  } catch (error) {
    if (signal.aborted) return;
    throw new CliError(`Request to ${path} failed: ${describe(error)}`, true);
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
  if (!response.body) return;

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  try {
    for (;;) {
      let chunk: Awaited<ReturnType<typeof reader.read>>;
      try {
        chunk = await reader.read();
      } catch (error) {
        if (signal.aborted) return;
        throw new CliError(`Event stream from ${path} failed: ${describe(error)}`, true);
      }
      if (chunk.done) return;
      buffer += chunk.value;
      let end: number;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        const event = parseBlock(block);
        if (event) onEvent(event);
      }
    }
  } finally {
    reader.cancel().catch(() => {});
  }
}

function parseBlock(block: string): SseEvent | undefined {
  let event = 'message';
  let id: string | undefined;
  const data: string[] = [];
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) event = line.slice(6).trim();
    else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
    else if (line.startsWith('id:')) id = line.slice(3).trim();
  }
  if (!data.length && event === 'message') return undefined;
  return { event, data: data.join('\n'), id };
}
