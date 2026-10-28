import { describe, expect, test } from 'bun:test';
import { parseTimeout, waitSettings } from '../src/wait';
import { ID, fakeFetch, health, pl } from './helpers';

waitSettings.fallbackMs = 1;
waitSettings.reconnectMs = 1;
waitSettings.errorMs = 1;

const alpha = {
  id: ID,
  name: 'alpha',
  description: null,
  repo: null,
  health: health(),
  stages: [],
};

const run = (
  number: number,
  status: string,
  overrides: Record<string, unknown> = {},
) => ({
  id: ID,
  number,
  status,
  triggerKind: 'Push',
  versionNumber: 4,
  versionStamp: `2026.10.28.${number}`,
  createdAt: '2026-10-28T10:00:00Z',
  finishedAt: null,
  heldAt: null,
  supersededByNumber: null,
  rerunOfNumber: null,
  configSnapshot: null,
  versionId: ID,
  commitSha: null,
  input: { sha: 'abcdef1234' },
  continuable: true,
  stages: [],
  config: null,
  ...overrides,
});

const events = { stream: ['event: hello\ndata: {"lastEventId":"1-0"}\n\n'] };

describe('wait', () => {
  test('--timeout parses durations', () => {
    expect(parseTimeout('90s')).toBe(90_000);
    expect(parseTimeout('30m')).toBe(1_800_000);
    expect(parseTimeout('2h')).toBe(7_200_000);
    expect(parseTimeout('5')).toBe(5_000);
    expect(() => parseTimeout('x')).toThrow('Invalid --timeout');
  });

  test('wait run follows a superseded run to the end, unless --no-follow', async () => {
    const detail = (n: number) =>
      n === 14
        ? run(14, 'Superseded', { supersededByNumber: 15 })
        : n === 15
          ? run(15, 'Superseded', { supersededByNumber: 16 })
          : run(16, 'Succeeded');
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      const n = Number(req.path.split('/').at(-1));
      return { body: detail(n) };
    });
    const followed = await pl(['wait', 'run', 'alpha#14'], { fetch });
    expect(followed.code).toBe(0);
    expect(followed.stdout).toBe('alpha#16  Succeeded\n');
    expect(followed.stderr).toContain('alpha#14 superseded by #15, following');
    const stopped = await pl(['wait', 'run', 'alpha#14', '--no-follow'], { fetch });
    expect(stopped.code).toBe(1);
    expect(stopped.stdout).toBe('alpha#14  Superseded\n');
  });

  test('a step waiting for a person ends the wait with exit 3; a step wait reports its status', async () => {
    const stages = [
      {
        index: 0,
        when: [],
        steps: [
          {
            name: 'build',
            stepVersion: 1,
            status: 'succeeded',
            reason: null,
            attemptCount: 1,
            latestAttempt: 1,
            succeededAttempt: 1,
            startedAt: null,
            finishedAt: null,
            blocked: false,
          },
          {
            name: 'approve',
            stepVersion: 1,
            status: 'waiting',
            reason: null,
            attemptCount: 1,
            latestAttempt: 1,
            succeededAttempt: null,
            startedAt: null,
            finishedAt: null,
            blocked: false,
            waitingFor: ['person'],
          },
        ],
      },
    ];
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      return { body: run(7, 'Running', { stages }) };
    });
    const waiting = await pl(['wait', 'run', 'alpha#7'], { fetch });
    expect(waiting.code).toBe(3);
    expect(waiting.stdout).toBe('alpha#7/approve  waiting for input\n');
    const step = await pl(['wait', 'run', 'alpha#7/build'], { fetch });
    expect(step.code).toBe(0);
    expect(step.stdout).toBe('alpha#7/build  succeeded\n');
  });

  test('wait push finds the run by its input sha, prints its address, and reports a failed reconcile', async () => {
    let calls = 0;
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      if (req.path.startsWith(`/api/pipelines/${ID}/runs?`))
        return {
          body: {
            items: [run(21, 'Running', { input: { sha: 'ffff' } }), run(20, 'Running')],
            offset: 0,
            limit: 15,
            total: 2,
          },
        };
      if (req.path.endsWith('/runs/21'))
        return { body: run(21, 'Running', { input: { sha: 'ffff' } }) };
      if (req.path.endsWith('/runs/20'))
        return { body: run(20, ++calls > 1 ? 'Succeeded' : 'Running') };
      return { status: 404 };
    });
    const started = await pl(['wait', 'push', 'alpha', 'abcdef'], { fetch });
    expect(started.code).toBe(0);
    expect(started.stdout).toBe('alpha#20\n');
    const finished = await pl(
      ['wait', 'push', 'alpha', 'ABCDEF', '--until', 'finished'],
      { fetch },
    );
    expect(finished.code).toBe(0);
    expect(finished.stdout).toBe('alpha#20  Succeeded\n');
    expect(finished.stderr).toContain('alpha#20 is the run of abcdef');

    const { fetch: failing } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      if (req.path.startsWith(`/api/pipelines/${ID}/runs?`))
        return { body: { items: [], offset: 0, limit: 15, total: 0 } };
      return {
        body: {
          result: 'Error',
          reconcileSha: 'abcdef999',
          problems: ['step x: image empty'],
          reconcileInProgress: false,
          lastReconciledSha: null,
          reconciledVersion: null,
          pending: null,
        },
      };
    });
    const failed = await pl(['wait', 'push', 'alpha', 'abcdef'], { fetch: failing });
    expect(failed.code).toBe(1);
    expect(failed.stdout).toBe('reconcile of abcdef failed: step x: image empty\n');
  });

  test('a retryable failure is retried; --timeout gives up with 124', async () => {
    let calls = 0;
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      return ++calls < 3
        ? { status: 503, body: { errors: [{ message: 'draining' }] } }
        : { body: run(5, 'Failed') };
    });
    const result = await pl(['wait', 'run', 'alpha#5'], { fetch });
    expect(result.code).toBe(1);
    expect(result.stdout).toBe('alpha#5  Failed\n');
    expect(result.stderr).toContain('server unavailable (draining); retrying');

    const { fetch: forever } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      return { body: run(5, 'Running') };
    });
    const timedOut = await pl(['wait', 'run', 'alpha#5', '--timeout', '0.05s'], {
      fetch: forever,
      sleep: (ms) => new Promise((r) => setTimeout(r, ms)),
    });
    expect(timedOut.code).toBe(124);
    expect(timedOut.stderr).toContain('Timed out after 0.05s (alpha#5: Running).');
  });
});
