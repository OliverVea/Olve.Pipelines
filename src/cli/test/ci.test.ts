import { describe, expect, test } from 'bun:test';
import type {
  CiRun,
  CiRunStep,
  CiReliabilityReport,
  CiRunSummary,
  Pipeline,
  PullDetail,
  Queue,
  QueueEntry,
} from '@olve/api';
import {
  formatCiAddress,
  parseAddress,
  parsePull,
  parseStepAddress,
} from '../src/address';
import { logRetryMs } from '../src/commands/ci';
import { waitSettings } from '../src/wait';
import { ID, fakeFetch, health, pl } from './helpers';

waitSettings.fallbackMs = 1;
waitSettings.reconnectMs = 1;
waitSettings.errorMs = 1;
logRetryMs.value = 1;

const alpha = {
  id: ID,
  name: 'alpha',
  description: null,
  repo: null,
  health: health(),
  stages: [],
} satisfies Pipeline;

const HEAD = 'aaaaaaa1111111';
const NEW_HEAD = 'bbbbbbb2222222';

const summary = (
  number: number,
  status: CiRunSummary['status'],
  overrides: Partial<CiRunSummary> = {},
): CiRunSummary => ({
  id: ID,
  number,
  kind: 'pull',
  status,
  pulls: [12],
  testedSha: 'ccccccc3333333',
  tree: 'ddddddd4444444',
  mainSha: 'eeeeeee5555555',
  createdAt: '2026-11-15T10:00:00Z',
  finishedAt: status === 'Running' ? null : '2026-11-15T10:03:07Z',
  supersededByNumber: null,
  cancelReason: null,
  steps: [
    { name: 'build', stage: 0, status: 'succeeded' },
    { name: 'test', stage: 1, status: status === 'Failed' ? 'failed' : 'succeeded' },
  ],
  ...overrides,
});

const step = (name: string, overrides: Partial<CiRunStep> = {}): CiRunStep => ({
  name,
  status: 'succeeded',
  reason: null,
  attemptCount: 1,
  latestAttempt: 1,
  succeededAttempt: 1,
  startedAt: '2026-11-15T10:00:00Z',
  finishedAt: '2026-11-15T10:01:00Z',
  reusedFrom: null,
  ...overrides,
});

const ciRun = (
  number: number,
  status: CiRun['status'],
  overrides: Partial<CiRun> = {},
  head = HEAD,
): CiRun => ({
  ...summary(number, status),
  pullHeads: [{ number: 12, title: 'Add CI', branch: 'feat/ci', headSha: head }],
  definitionSha: 'eeeeeee5555555',
  stages: [
    {
      index: 0,
      when: [],
      steps: [step('build', { reusedFrom: { run: 3, step: 'build', attempt: 1 } })],
    },
    {
      index: 1,
      when: [],
      steps: [
        step('test', {
          status: status === 'Failed' ? 'failed' : 'succeeded',
          reason: status === 'Failed' ? 'exit 1' : null,
        }),
      ],
    },
  ],
  problems: [],
  ...overrides,
});

const pull = (overrides: Partial<PullDetail> = {}): PullDetail => ({
  number: 12,
  title: 'Add CI',
  branch: 'feat/ci',
  headSha: HEAD,
  draft: false,
  state: 'open',
  conflict: false,
  stale: false,
  latestRun: summary(7, 'Succeeded'),
  queue: null,
  preview: null,
  updatedAt: '2026-11-15T09:59:00Z',
  runs: [summary(7, 'Succeeded'), summary(6, 'Superseded', { supersededByNumber: 7 })],
  ...overrides,
});

const entry = (overrides: Partial<QueueEntry> = {}): QueueEntry => ({
  pull: 12,
  title: 'Add CI',
  branch: 'feat/ci',
  headSha: HEAD,
  state: 'queued',
  position: 1,
  enqueuedAt: '2026-11-15T10:00:00Z',
  via: 'api',
  enqueuedBy: 'oliver',
  runs: [],
  finishedAt: null,
  reason: null,
  failedStep: null,
  ...overrides,
});

const events = {
  stream: ['event: hello\ndata: {"type":"hello","lastEventId":"1-0"}\n\n'],
};

const ciPath = `/api/pipelines/${ID}/ci`;

describe('ci addresses', () => {
  test('a CI run is <pipeline>#ci<run>, with steps, attempts and paths as for deploy runs', () => {
    expect(parseAddress('Olve.Pipelines v2#ci12/test@2/out/a#ci3.txt', 'ci')).toEqual({
      pipeline: 'Olve.Pipelines v2',
      run: 12,
      step: 'test',
      attempt: 2,
      path: 'out/a#ci3.txt',
    });
    expect(formatCiAddress('p', 12, 'build[os=linux]', 1)).toBe(
      'p#ci12/build[os=linux]@1',
    );
    expect(parseStepAddress('p#ci12/build[os=linux]@1', false, 'ci').step).toBe(
      'build[os=linux]',
    );
  });

  test('a deploy address is not a CI address, nor the other way round', () => {
    const rows: [string, 'cd' | 'ci', string][] = [
      [
        'alpha#12',
        'ci',
        "Expected a CI run address like <pipeline>#ci<run>[/<step>[@<attempt>]] (e.g. olve-pipelines#ci12/test@1), got 'alpha#12'. '#12' is a deploy run; CI run 12 is '#ci12'.",
      ],
      [
        'alpha#ci12/test',
        'cd',
        "Expected an address like <pipeline>#<run>[/<step>[@<attempt>]] (e.g. olve-pipelines#412/deploy-beta@1), got 'alpha#ci12/test'. 'alpha#ci12/test' is a CI run: use `pl ci …` (`pl ci get`, `pl ci logs`, `pl wait ci`).",
      ],
      ['alpha#ci0', 'ci', "Run number in 'alpha#ci0' must be a positive integer."],
    ];
    for (const [text, kind, message] of rows)
      expect(() => parseAddress(text, kind)).toThrow(message);
  });

  test('a pull request is 12 or #12', () => {
    expect(parsePull('12')).toBe(12);
    expect(parsePull('#12')).toBe(12);
    expect(() => parsePull('ci12')).toThrow(
      "Expected a pull request number (e.g. 12 or #12), got 'ci12'.",
    );
  });
});

describe('ci commands', () => {
  test('ls lists pull requests with their latest run; --closed asks for closed ones', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      return {
        body: {
          items: [
            pull({
              latestRun: summary(7, 'Failed'),
              stale: true,
              draft: true,
            }),
            pull({ number: 9, title: 'Fix', conflict: true, latestRun: null }),
          ],
          offset: 0,
          limit: 20,
          total: 2,
        },
      };
    });
    const result = await pl(['ci', 'ls', 'alpha'], { fetch });
    expect(result.code).toBe(0);
    const lines = result.stdout.split('\n');
    expect(lines[0]).toMatch(
      /^PR +STATUS +STEPS +RUN +COMMIT +STARTED +TOOK +TITLE +NOTE$/,
    );
    expect(lines[1]).toMatch(
      /^#12 +Failed +1\/2 +#ci7 +aaaaaaa +\S+ \S+ +3m07s +Add CI +draft; stale; failed: test$/,
    );
    expect(lines[2]).toMatch(/^#9 +- +- +- +aaaaaaa +- +- +Fix +conflict$/);
    expect(result.stdout).toContain('page 0 · 2 of 2 pull request(s)');
    expect(requests.at(-1)!.path).toBe(`${ciPath}/pulls?state=open&offset=0&limit=20`);

    await pl(['ci', 'ls', 'alpha', '--closed'], { fetch });
    expect(requests.at(-1)!.path).toContain('state=closed');
  });

  test('show prints the pull request and its runs', async () => {
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `${ciPath}/pulls/12`)
        return {
          body: pull({
            stale: true,
            runs: [
              summary(8, 'Succeeded', { kind: 'close' }),
              summary(7, 'Succeeded'),
              summary(6, 'Superseded', { supersededByNumber: 7 }),
            ],
          }),
        };
      return { status: 404 };
    });
    const result = await pl(['ci', 'show', 'alpha', '#12'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toContain('alpha #12  Add CI\n');
    expect(result.stdout).toContain(
      'note:      stale: tested on a main that has moved since',
    );
    expect(result.stdout).toMatch(/\n#ci8 +close +Succeeded /);
    expect(result.stdout).toMatch(/\n#ci7 +pull +Succeeded +2\/2 +ccccccc +eeeeeee /);
    expect(result.stdout).toMatch(/\n#ci6 +pull +Superseded .*superseded by #ci7\n/);
  });

  test('get shows a CI run with its reused steps; cancel and rerun print addresses', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `${ciPath}/runs/7`) return { body: ciRun(7, 'Failed') };
      if (req.path === `${ciPath}/runs/7/cancel`) return { body: ciRun(7, 'Cancelled') };
      if (req.path === `${ciPath}/pulls/12/runs`) return { body: ciRun(8, 'Running') };
      return { status: 404 };
    });
    const got = await pl(['ci', 'get', 'alpha#ci7'], { fetch });
    expect(got.code).toBe(0);
    expect(got.stdout).toStartWith(
      'alpha#ci7  Failed\npull:      #12 Add CI (feat/ci at aaaaaaa)\n',
    );
    expect(got.stdout).toContain(
      "tested:    ccccccc (tree ddddddd) on main eeeeeee · definition main's",
    );
    expect(got.stdout).toMatch(
      / {2}build +succeeded +@1\/1 +1m00s +reused alpha#ci3\/build@1\n/,
    );
    expect(got.stdout).toMatch(/ {2}test +failed +@1\/1 +1m00s +exit 1\n/);

    const wrong = await pl(['ci', 'get', 'alpha#7'], { fetch });
    expect(wrong.code).toBe(1);
    expect(wrong.stderr).toContain("CI run 7 is '#ci7'");

    const cancelled = await pl(['ci', 'cancel', 'alpha#ci7'], { fetch });
    expect(cancelled.stdout).toBe('alpha#ci7  Cancelled\n');
    expect(requests.at(-1)!.method).toBe('POST');

    const rerun = await pl(['ci', 'rerun', 'alpha', '12'], { fetch });
    expect(rerun.stdout).toBe('alpha#ci8\nstarted CI run #ci8 of #12 (Running)\n');

    const before = requests.length;
    const usage = await pl(['ci', 'rerun', 'alpha', 'x'], { fetch });
    expect(usage.code).toBe(2);
    expect(requests.length).toBe(before);
  });

  test('a run without stages says why', async () => {
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `${ciPath}/runs/4`)
        return {
          body: ciRun(4, 'Failed', { stages: [], problems: ['describe failed: boom'] }),
        };
      return { body: ciRun(5, 'Running', { stages: [], definitionSha: 'fffffff' }) };
    });
    const failed = await pl(['ci', 'get', 'alpha#ci4'], { fetch });
    expect(failed.stdout).toEndWith('problem:   describe failed: boom\n');
    const building = await pl(['ci', 'get', 'alpha#ci5'], { fetch });
    expect(building.stdout).toContain('definition built from fffffff');
    expect(building.stdout).toEndWith(
      '(the definition is not built and described yet)\n',
    );
  });

  test('output, files and logs address a CI step', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith(`${ciPath}/runs/7/steps/test/output`))
        return {
          body: {
            run: 7,
            step: 'test',
            attempt: 2,
            status: 'succeeded',
            output: { ok: true },
            outputKey: null,
          },
        };
      if (req.path.startsWith(`${ciPath}/runs/7/steps/test/files`))
        return { body: { run: 7, step: 'test', attempt: 2, files: [] } };
      if (req.path.startsWith(`${ciPath}/runs/7/steps/test/logs`))
        return {
          body: {
            run: 7,
            step: 'test',
            attempt: 2,
            status: 'succeeded',
            complete: true,
            text: 'ok',
          },
        };
      return { status: 404 };
    });
    const output = await pl(['ci', 'output', 'alpha#ci7/test@2'], { fetch });
    expect(output.stdout).toBe('{\n  "ok": true\n}\n');
    expect(requests.at(-1)!.path).toBe(`${ciPath}/runs/7/steps/test/output?attempt=2`);
    const files = await pl(['ci', 'files', 'alpha#ci7/test'], { fetch });
    expect(files.stdout).toBe('alpha#ci7/test@2\n(no files)\n');
    const logs = await pl(['ci', 'logs', 'alpha#ci7/test'], { fetch });
    expect(logs.stdout).toBe('ok\n');
  });

  test('logs --follow streams until the attempt ends, reconnecting without repeating', async () => {
    let connections = 0;
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith(`${ciPath}/runs/7/steps/test/logs/stream`)) {
        connections += 1;
        return connections === 1
          ? {
              stream: [
                'event: log\ndata: {"type":"log","text":"one\\n","reset":true}\n\n',
                'event: log\ndata: {"type":"log","text":"two","reset":false}\n\n',
              ],
              ends: true,
            }
          : {
              stream: [
                'event: log\ndata: {"type":"log","text":"","reset":true}\n\n',
                'event: log\ndata: {"type":"log","text":"one\\ntwo\\nthree","reset":true}\n\n',
                'event: end\ndata: {"type":"end","status":"succeeded"}\n\n',
              ],
              ends: true,
            };
      }
      return { status: 404 };
    });
    const result = await pl(['ci', 'logs', 'alpha#ci7/test', '-f'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toBe('one\ntwo\nthree\n');
    expect(connections).toBe(2);

    const { fetch: missing } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `${ciPath}/runs/7`) return { body: ciRun(7, 'Running') };
      return { status: 404, body: { errors: [{ message: 'No step tset.' }] } };
    });
    const typo = await pl(['ci', 'logs', 'alpha#ci7/tset', '-f'], { fetch: missing });
    expect(typo.code).toBe(1);
    expect(typo.stderr).toBe("error: CI run #ci7 has no step 'tset'.");
  });
});

describe('merge queue', () => {
  test('merge enqueues the pull request; dequeue takes it out', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `${ciPath}/queue` && req.method === 'POST')
        return { body: entry({ position: 2 }) };
      if (req.path === `${ciPath}/queue/12` && req.method === 'DELETE')
        return {
          body: entry({ state: 'removed', position: null, reason: 'dequeued by oliver' }),
        };
      return { status: 404 };
    });
    const merged = await pl(['ci', 'merge', 'alpha', '#12'], { fetch });
    expect({ code: merged.code, stdout: merged.stdout }).toEqual({
      code: 0,
      stdout: 'alpha #12  queued #2\n',
    });
    expect(requests.at(-1)!.body).toEqual({ pull: 12 });

    const dequeued = await pl(['ci', 'dequeue', 'alpha', '12'], { fetch });
    expect(dequeued.stdout).toBe('alpha #12  removed (dequeued by oliver)\n');

    const before = requests.length;
    const usage = await pl(['ci', 'merge', 'alpha', 'x'], { fetch });
    expect(usage.code).toBe(2);
    expect(requests.length).toBe(before);
  });

  test('a refused merge prints the problem, exit 1', async () => {
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      return {
        status: 409,
        body: {
          status: 409,
          code: 'conflict',
          errors: [{ message: 'Pull request #7 is a draft.' }],
        },
      };
    });
    const result = await pl(['ci', 'merge', 'alpha', '7'], { fetch });
    expect({ code: result.code, stderr: result.stderr }).toEqual({
      code: 1,
      stderr: 'error: Pull request #7 is a draft.',
    });
  });

  test('queue lists the entries in order, then the finished ones', async () => {
    const queue: Queue = {
      entries: [
        entry({ state: 'testing', position: 1, runs: [9] }),
        entry({
          pull: 14,
          title: 'Fix',
          position: 2,
          via: 'auto-merge',
          enqueuedBy: null,
        }),
      ],
      finished: [
        entry({
          pull: 9,
          title: 'Old',
          state: 'removed',
          position: null,
          runs: [8, 7],
          finishedAt: '2026-11-15T09:00:00Z',
          reason: 'test failed',
          failedStep: 'test',
        }),
      ],
    };
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `${ciPath}/queue`) return { body: queue };
      return { status: 404 };
    });
    const result = await pl(['ci', 'queue', 'alpha'], { fetch });
    expect(result.code).toBe(0);
    const lines = result.stdout.split('\n');
    expect(lines[0]).toMatch(/^PR +STATE +RUNS +VIA +ENQUEUED +TITLE +NOTE$/);
    expect(lines[1]).toMatch(/^#12 +testing +#ci9 +api \(oliver\) +\S+ \S+ +Add CI$/);
    expect(lines[2]).toMatch(/^#14 +queued #2 +- +auto-merge +\S+ \S+ +Fix$/);
    expect(lines[4]).toBe('finished in the last day:');
    expect(lines[5]).toMatch(/^PR +STATE +RUNS +VIA +FINISHED +TITLE +NOTE$/);
    expect(lines[6]).toMatch(
      /^#9 +removed +#ci8 #ci7 +api \(oliver\) .* Old +test failed: test$/,
    );

    const { fetch: empty } = fakeFetch((req) =>
      req.path === '/api/pipelines'
        ? { body: [alpha] }
        : { body: { entries: [], finished: [] } },
    );
    expect((await pl(['ci', 'queue', 'alpha'], { fetch: empty })).stdout).toBe(
      '(the queue is empty)\n',
    );
  });

  test('wait ci --queue: merged exits 0, removed or absent exits 1 with why', async () => {
    const rows: [Queue, number, string][] = [
      [
        { entries: [], finished: [entry({ state: 'merged', position: null })] },
        0,
        'alpha #12  merged\n',
      ],
      [
        {
          entries: [],
          finished: [
            entry({
              state: 'removed',
              position: null,
              reason: 'test failed',
              failedStep: 'test',
            }),
          ],
        },
        1,
        'alpha #12  removed (test failed: test)\n',
      ],
      [
        { entries: [entry({ pull: 14 })], finished: [] },
        1,
        'alpha #12  not in the queue\n',
      ],
    ];
    for (const [queue, code, stdout] of rows) {
      const { fetch } = fakeFetch((req) => {
        if (req.path === '/api/pipelines') return { body: [alpha] };
        if (req.path.startsWith('/api/events')) return events;
        return { body: queue };
      });
      const result = await pl(['wait', 'ci', 'alpha', '12', '--queue'], { fetch });
      expect({ code: result.code, stdout: result.stdout }).toEqual({ code, stdout });
    }
  });

  test('wait ci --queue follows the entry until it merges; a re-enqueued PR is its open entry', async () => {
    let checks = 0;
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      checks += 1;
      const old = entry({ state: 'removed', position: null, reason: 'pushed' });
      const open =
        checks === 1
          ? entry({ position: 2 })
          : checks === 2
            ? entry({ state: 'testing', runs: [9] })
            : undefined;
      return {
        body: open
          ? { entries: [open], finished: [old] }
          : { entries: [], finished: [entry({ state: 'merged', position: null }), old] },
      };
    });
    const result = await pl(['wait', 'ci', 'alpha', '12', '--queue'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toBe('alpha #12  merged\n');
    expect(result.stderr).toContain('alpha #12: queued #2');
    expect(result.stderr).toContain('alpha #12: testing in #ci9');

    const both = await pl(['wait', 'ci', 'alpha', '12', '--queue', '--no-follow'], {
      fetch,
    });
    expect(both.code).toBe(2);
  });
});

describe('wait ci', () => {
  const serve = (
    pulls: () => PullDetail,
    runs: (n: number) => CiRun,
  ): ReturnType<typeof fakeFetch> =>
    fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith('/api/events')) return events;
      if (req.path === `${ciPath}/pulls/12`) return { body: pulls() };
      const n = Number(/\/runs\/(\d+)$/.exec(req.path)?.[1]);
      return { body: runs(n) };
    });

  test('the outcome of the latest run decides the exit code', async () => {
    const rows: [CiRun, PullDetail, number, string][] = [
      [ciRun(7, 'Succeeded'), pull(), 0, 'alpha#ci7  Succeeded\n'],
      [ciRun(7, 'Failed'), pull(), 1, 'alpha#ci7  Failed (test)\n'],
      [
        ciRun(7, 'Cancelled', { cancelReason: 'closed' }),
        pull({ state: 'closed' }),
        1,
        'alpha#ci7  Cancelled (closed)\n',
      ],
      [
        ciRun(7, 'Failed'),
        pull({ conflict: true, headSha: NEW_HEAD }),
        1,
        'alpha #12  conflict: no CI run until a push lets it merge onto main\n',
      ],
      [ciRun(7, 'Succeeded'), pull({ conflict: true }), 0, 'alpha#ci7  Succeeded\n'],
      [
        ciRun(7, 'Running'),
        pull({ state: 'closed', latestRun: null }),
        1,
        'alpha #12  closed without a CI run\n',
      ],
    ];
    for (const [run, detail, code, stdout] of rows) {
      const { fetch } = serve(
        () => detail,
        () => run,
      );
      const result = await pl(['wait', 'ci', 'alpha', '12'], { fetch });
      expect({ code: result.code, stdout: result.stdout }).toEqual({ code, stdout });
    }
  });

  test('a superseded run is followed to the run that superseded it, unless --no-follow', async () => {
    let fetches = 0;
    const runs = (n: number) => {
      if (n !== 7) return ciRun(8, 'Succeeded');
      fetches += 1;
      return fetches > 1
        ? ciRun(7, 'Superseded', { supersededByNumber: 8 })
        : ciRun(7, 'Running');
    };
    const { fetch } = serve(() => pull({ latestRun: summary(7, 'Running') }), runs);
    const followed = await pl(['wait', 'ci', 'alpha', '12'], { fetch });
    expect(followed.code).toBe(0);
    expect(followed.stdout).toBe('alpha#ci8  Succeeded\n');
    expect(followed.stderr).toContain('alpha#ci7 superseded by #ci8, following');

    fetches = 0;
    const stopped = await pl(['wait', 'ci', 'alpha', '12', '--no-follow'], { fetch });
    expect(stopped.code).toBe(1);
    expect(stopped.stdout).toBe('alpha#ci7  Superseded (by #ci8)\n');
  });

  test("right after a push the previous head's result is not the answer", async () => {
    let checks = 0;
    const { fetch } = serve(
      () => {
        checks += 1;
        return pull({
          headSha: NEW_HEAD,
          latestRun: checks < 3 ? summary(7, 'Failed') : summary(8, 'Succeeded'),
        });
      },
      (n) => (n === 7 ? ciRun(7, 'Failed') : ciRun(8, 'Succeeded', {}, NEW_HEAD)),
    );
    const result = await pl(['wait', 'ci', 'alpha', '12'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toBe('alpha#ci8  Succeeded\n');
    expect(result.stderr).toContain(
      'alpha #12: waiting for a CI run of bbbbbbb (alpha#ci7 tested aaaaaaa)',
    );
  });

  test('--timeout gives up with 124', async () => {
    const { fetch } = serve(
      () => pull({ latestRun: summary(7, 'Running') }),
      () => ciRun(7, 'Running'),
    );
    const result = await pl(['wait', 'ci', 'alpha', '12', '--timeout', '0.05s'], {
      fetch,
      sleep: (ms) => new Promise((r) => setTimeout(r, ms)),
    });
    expect(result.code).toBe(124);
    expect(result.stderr).toContain(
      'Timed out after 0.05s (alpha#ci7: Running (2/2 steps)).',
    );
  });
});

describe('ci reliability', () => {
  const report: CiReliabilityReport = {
    fromRun: 3,
    toRun: 12,
    runs: 10,
    requestedRuns: 200,
    steps: [
      {
        step: 'test',
        attempts: 10,
        failures: 4,
        ownFailures: 3,
        infrastructureFailures: 1,
        timeouts: 0,
        flakyFailures: 1,
        reproducedFailures: 0,
        caughtFailures: 2,
        unverifiedFailures: 0,
        flakyRate: 0.1,
        flaky: [
          {
            failed: { run: 7, step: 'test', attempt: 1 },
            passed: { run: 8, step: 'test', attempt: 1 },
            pulls: [12],
            failedAt: '2026-11-15T10:00:00Z',
          },
        ],
      },
    ],
    notes: [],
  };

  test('lists steps by flakiness with each flaky failure and where it passed; --runs is passed on', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.startsWith(`${ciPath}/runs/reliability`)) return { body: report };
      return { status: 404 };
    });
    const result = await pl(['ci', 'reliability', 'alpha', '--runs', '50'], { fetch });
    expect(result.code).toBe(0);
    const lines = result.stdout.split('\n');
    expect(lines[0]).toBe('CI runs #ci3–#ci12 (10 of the latest 200 asked for)');
    expect(lines[2]).toMatch(
      /^STEP +ATTEMPTS +OWN +INFRA +TIMEOUT +FLAKY +RATE +REPRODUCED +CAUGHT +UNVERIFIED$/,
    );
    expect(lines[3]).toMatch(/^test +10 +3 +1 +0 +1 +10\.0% +0 +2 +0$/);
    expect(lines[5]).toBe(
      'flaky failures (failed, then passed on the same tree and code):',
    );
    expect(lines[6]).toMatch(
      /^ {2}alpha#ci7\/test@1 +passed in alpha#ci8\/test@1 +#12 +\S+/,
    );
    expect(requests.at(-1)!.url).toContain('runs=50');
  });

  test('--runs must be a number', async () => {
    const { fetch } = fakeFetch(() => ({ body: [alpha] }));
    const result = await pl(['ci', 'reliability', 'alpha', '--runs', 'x'], { fetch });
    expect(result.stderr).toBe('error: --runs must be a number.');
  });
});
