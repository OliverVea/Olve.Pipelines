import { describe, expect, test } from 'bun:test';
import { ID, fakeFetch, health, pl } from './helpers';

const alpha = {
  id: ID,
  name: 'alpha',
  description: null,
  repo: null,
  health: health(),
  stages: [],
};

const sse = (event: string, data: Record<string, unknown>) =>
  `event: ${event}\ndata: ${JSON.stringify({ type: event, ...data })}\n\n`;

describe('watch', () => {
  test("prints a pipeline's change events until the stream ends", async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      return {
        stream: [
          sse('hello', { lastEventId: '1-0' }),
          sse('ping', {}),
          sse('run', { pipelineId: ID, run: 15, status: 'Running' }),
          sse('resync', { reason: 'missed-events' }),
        ],
        ends: true,
      };
    });
    const out = await pl(['watch', 'alpha'], { fetch });
    expect(out.code).toBe(0);
    expect(requests.at(-1)!.path).toBe(`/api/events?pipeline=${ID}`);
    expect(requests.at(-1)!.headers.get('Accept')).toBe('text/event-stream');
    const lines = out.stdout.trimEnd().split('\n');
    expect(lines.map((l) => l.slice(10))).toEqual([
      'run       run=15 status=Running',
      'resync    missed events: refetch what you are looking at',
    ]);
    expect(out.stderr).toContain('The event stream ended');

    const json = await pl(['watch', 'alpha', '--json'], { fetch });
    expect(JSON.parse(json.stdout.split('\n')[0]!)).toEqual({
      event: 'run',
      data: { type: 'run', pipelineId: ID, run: 15, status: 'Running' },
    });
  });

  test("a refused stream is the server's answer, not a quiet end", async () => {
    const { fetch } = fakeFetch(() => ({ status: 401 }));
    const out = await pl(['watch'], { fetch });
    expect(out.code).toBe(1);
    expect(out.stderr).toContain('Run `pl login`');

    const missing = fakeFetch(() => ({
      status: 404,
      body: { errors: [{ message: 'No pipeline here.' }] },
    }));
    const notFound = await pl(['watch'], { fetch: missing.fetch });
    expect(notFound.code).toBe(1);
    expect(notFound.stderr).toBe('error: No pipeline here.');
  });

  test('a lost connection fails the watch', async () => {
    const { fetch } = fakeFetch(() => new TypeError('connection refused'));
    const out = await pl(['watch'], { fetch });
    expect(out.code).toBe(1);
    expect(out.stderr).toContain('The event stream failed: connection refused');
  });
});
