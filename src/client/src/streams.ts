import {
  eventsStream,
  runStepsLogStream,
  type ChangeEvent,
  type LogEvent,
  type Ping,
  type StreamHello,
  type StreamResync,
} from '@olve/api';
import type { Client } from '@olve/api/client';
import { ApiError } from './client';

/** A change to something a view shows; the other change events are about the stream itself. */
export type ChangeNotice = Exclude<ChangeEvent, StreamHello | StreamResync | Ping>;

/**
 * `hello` (every connect and reconnect) and `resync` (a stale `Last-Event-ID`): nothing is replayed,
 * so whatever a client shows may be stale and is reloaded.
 */
export const reloadsAll = (e: ChangeEvent): e is StreamHello | StreamResync =>
  e.type === 'hello' || e.type === 'resync';

export const isNotice = (e: ChangeEvent): e is ChangeNotice =>
  e.type !== 'ping' && !reloadsAll(e);

/** The instance's change events (one pipeline's with `pipeline`), over one connection. */
export function changeEvents(
  client: Client,
  signal: AbortSignal,
  pipeline?: string,
): AsyncGenerator<ChangeEvent> {
  return readStream(client, signal, (sse) =>
    eventsStream({ client, query: pipeline ? { pipeline } : undefined, ...sse }),
  );
}

/** An attempt's log as it grows (default: the step's latest attempt), over one connection. */
export function logEvents(
  client: Client,
  signal: AbortSignal,
  path: { pipelineId: string; number: number; step: string },
  attempt?: number,
): AsyncGenerator<LogEvent> {
  return readStream(client, signal, (sse) =>
    runStepsLogStream({
      client,
      path,
      query: attempt === undefined ? undefined : { attempt },
      ...sse,
    }),
  );
}

type SseOptions = {
  headers: Record<string, string>;
  fetch: typeof fetch;
  onSseError: (error: unknown) => void;
  sseMaxRetryAttempts: number;
};

/**
 * One connection of a generated SSE operation, its events parsed. It ends when the server closes the
 * stream or `signal` aborts; a non-2xx answer throws an {@link ApiError}, a lost connection its error.
 * The generated client would retry by itself and swallow both: reconnecting is the caller's.
 * Stopping early (`break`) closes the connection too.
 */
async function* readStream<T>(
  client: Client,
  signal: AbortSignal,
  open: (sse: SseOptions) => Promise<{ stream: AsyncGenerator<unknown> }>,
): AsyncGenerator<T> {
  const base = client.getConfig().fetch ?? globalThis.fetch;
  // The abort reaches the request through this fetch, not the generated client's `signal`: its
  // abort handler leaves a rejected `reader.cancel()` unhandled (fatal under Bun).
  const connection = new AbortController();
  const abort = () => connection.abort();
  signal.addEventListener('abort', abort, { once: true });
  let failure: unknown;
  try {
    if (signal.aborted) return;
    const { stream } = await open({
      headers: { Accept: 'text/event-stream' },
      fetch: (async (input: RequestInfo | URL, init?: RequestInit) => {
        const response = await base(input, { ...init, signal: connection.signal });
        if (!response.ok) throw await ApiError.from(response);
        return response;
      }) as typeof fetch,
      onSseError: (error) => {
        failure = error;
      },
      sseMaxRetryAttempts: 1,
    });
    for await (const data of stream) yield data as T;
    if (failure !== undefined && !signal.aborted) throw failure;
  } finally {
    signal.removeEventListener('abort', abort);
    connection.abort();
  }
}
