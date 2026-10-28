import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { type Io, run } from '../src/cli';
import type { ProcessRunner } from '../src/process';

export type Recorded = {
  method: string;
  url: string;
  path: string;
  headers: Headers;
  body: unknown;
};

type Reply =
  | {
      status?: number;
      statusText?: string;
      body?: unknown;
      headers?: Record<string, string>;
      /** Server-sent events: each string is one chunk; the stream then stays open until the request aborts. */
      stream?: string[];
    }
  | Error;

function parseBody(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/** A fetch stand-in that records each request and answers with `reply(request)`. */
export function fakeFetch(reply: (req: Recorded) => Reply) {
  const requests: Recorded[] = [];
  const fn = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    const text = await request.text();
    const recorded: Recorded = {
      method: request.method,
      url: request.url,
      path: new URL(request.url).pathname + new URL(request.url).search,
      headers: request.headers,
      body: text ? parseBody(text) : undefined,
    };
    requests.push(recorded);
    const r = reply(recorded);
    if (r instanceof Error) throw r;
    const status = r.status ?? 200;
    if (r.stream) {
      const chunks = r.stream;
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          for (const chunk of chunks) controller.enqueue(new TextEncoder().encode(chunk));
          const close = () => {
            try {
              controller.close();
            } catch {
              /* already closed */
            }
          };
          if (request.signal.aborted) close();
          else request.signal.addEventListener('abort', close, { once: true });
        },
      });
      return new Response(stream, {
        status,
        headers: { 'Content-Type': 'text/event-stream' },
      });
    }
    const body =
      r.body === undefined
        ? null
        : typeof r.body === 'string'
          ? r.body
          : JSON.stringify(r.body);
    return new Response(body, {
      status,
      statusText: r.statusText ?? '',
      headers: { 'Content-Type': 'application/json', ...(r.headers ?? {}) },
    });
  };
  return { fetch: fn as typeof fetch, requests };
}

/** Runs `pl` with captured output, an empty temp home (no profiles) and the given fetch. */
export async function pl(
  argv: string[],
  options: {
    fetch?: typeof fetch;
    env?: Record<string, string>;
    home?: string;
    sleep?: (ms: number) => Promise<void>;
    stdin?: string;
    run?: ProcessRunner;
    stdinIsTerminal?: boolean;
    prompt?: (text: string) => Promise<string>;
  } = {},
) {
  const home = options.home ?? mkdtempSync(join(tmpdir(), 'pl-test-'));
  const stdout: string[] = [];
  const stderr: string[] = [];
  const io: Io = {
    stdout: (text) => stdout.push(text),
    stderr: (line) => stderr.push(line),
    env: { HOME: home, ...(options.env ?? {}) },
    fetch: options.fetch,
    interactive: false,
    now: () => new Date('2026-10-28T12:00:00Z'),
    sleep: options.sleep ?? (async () => {}),
    stdoutBytes: (bytes) => stdout.push(new TextDecoder().decode(bytes)),
    stdin: async () => options.stdin ?? '',
    stdinIsTerminal: options.stdinIsTerminal ?? options.stdin === undefined,
    run: options.run,
    prompt: options.prompt,
  };
  const code = await run(argv, io);
  return { code, stdout: stdout.join(''), stderr: stderr.join('\n'), home };
}

export const ID = '11111111-1111-1111-1111-111111111111';

export const health = (overrides: Record<string, unknown> = {}) => ({
  status: 'Healthy',
  lastRun: null,
  lastSettledRun: null,
  runningRuns: 0,
  activation: null,
  draining: false,
  blockedSteps: [],
  heldRuns: [],
  pending: null,
  ...overrides,
});
