import { describe, expect, test } from 'bun:test';
import type { Pipeline } from '@olve/api';
import { ID, fakeFetch, health, pl } from './helpers';

const alpha = (overrides: Partial<Pipeline> = {}): Pipeline => ({
  id: ID,
  name: 'alpha',
  description: null,
  repo: 'OliverVea/alpha',
  health: health(),
  stages: [],
  ...overrides,
});

describe('pipeline list', () => {
  test('renders id, name and status; --json emits the payload', async () => {
    const { fetch } = fakeFetch(() => ({ body: [alpha()] }));
    const human = await pl(['pipeline', 'list'], { fetch });
    expect(human.code).toBe(0);
    expect(human.stdout).toContain('alpha');
    expect(human.stdout).toContain(ID);
    expect(human.stdout).toContain('Healthy');
    const json = await pl(['pipeline', 'list', '--json'], { fetch });
    expect(json.stdout.trim().startsWith('[')).toBe(true);
    expect(JSON.parse(json.stdout)[0].name).toBe('alpha');
  });

  test('--summaries adds the repo; the last run, blocked steps and activation render', async () => {
    const { fetch } = fakeFetch(() => ({
      body: [
        alpha({
          health: health({
            status: 'Unhealthy',
            blockedSteps: ['deploy-prod'],
            lastRun: {
              id: ID,
              number: 41,
              status: 'Failed',
              triggerKind: 'Push',
              versionNumber: 7,
              versionStamp: '2026.10.28.41',
              createdAt: '2026-10-28T10:00:00Z',
              finishedAt: null,
              heldAt: null,
              supersededByNumber: null,
              rerunOfNumber: null,
            },
            activation: {
              type: 'active',
              versionId: ID,
              versionNumber: 7,
              since: '2026-10-28T09:00:00Z',
            },
          }),
        }),
      ],
    }));
    const result = await pl(['pipeline', 'list', '--summaries'], { fetch });
    expect(result.stdout).toContain('Unhealthy');
    expect(result.stdout).toContain('#41 Failed');
    expect(result.stdout).toContain('deploy-prod');
    expect(result.stdout).toContain('active v7');
    expect(result.stdout).toContain('OliverVea/alpha');
  });
});

describe('pipeline get and switch', () => {
  test('resolves a name through the list, an id directly', async () => {
    const { fetch, requests } = fakeFetch((req) =>
      req.path === '/api/pipelines' ? { body: [alpha()] } : { body: alpha() },
    );
    const byName = await pl(['pipeline', 'get', 'alpha'], { fetch });
    expect(byName.code).toBe(0);
    expect(byName.stdout).toContain(`Id:   ${ID}`);
    expect(requests.map((r) => r.path)).toEqual([
      '/api/pipelines',
      `/api/pipelines/${ID}`,
    ]);
    requests.length = 0;
    await pl(['pipeline', 'get', ID], { fetch });
    expect(requests.map((r) => r.path)).toEqual([`/api/pipelines/${ID}`]);
  });

  test('an unknown name fails with the hint', async () => {
    const { fetch } = fakeFetch(() => ({ body: [alpha()] }));
    const result = await pl(['pipeline', 'get', 'beta'], { fetch });
    expect(result.code).toBe(1);
    expect(result.stderr).toContain("No pipeline named 'beta'");
  });

  test('switch posts the version and prints the activation', async () => {
    const { fetch, requests } = fakeFetch((req) =>
      req.method === 'POST'
        ? {
            body: {
              type: 'draining',
              fromVersionId: ID,
              fromVersionNumber: 6,
              toVersionId: ID,
              toVersionNumber: 7,
              cancelRunning: true,
              since: null,
            },
          }
        : { body: [alpha()] },
    );
    const result = await pl(['pipeline', 'switch', 'alpha', 'v7', '--cancel-running'], {
      fetch,
    });
    expect(result.code).toBe(0);
    expect(requests[1]!.body).toEqual({ version: 7, cancelRunning: true });
    expect(result.stdout).toBe('draining v6 -> v7 (cancelling running)\n');
    expect((await pl(['pipeline', 'switch', 'alpha', 'x'], { fetch })).stderr).toContain(
      'positive integer',
    );
  });
});

describe('pipeline manifest', () => {
  const manifest = JSON.stringify({
    name: 'alpha',
    input: null,
    models: {},
    secrets: [],
    triggers: [],
    stages: [],
    failureHandlers: [],
  });
  const version = {
    number: 3,
    manifestHash: 'sha256:ab',
    commitSha: '0123456789abcdef',
    createdAt: '2026-10-28T10:00:00Z',
    manifest,
  };

  test('defaults to the active version, summarizes it, and --json prints the manifest itself', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha()] };
      if (req.path.endsWith('/versions'))
        return {
          body: { activation: { type: 'active', versionNumber: 3 }, versions: [] },
        };
      if (req.path.endsWith('/versions/3/effective'))
        return {
          body: {
            version: 3,
            configSnapshot: 2,
            selected: { target: 'ssh' },
            manifest,
            cases: {},
          },
        };
      if (req.path.endsWith('/versions/3')) return { body: version };
      return { status: 404 };
    });
    const human = await pl(['pipeline', 'manifest', 'alpha'], { fetch });
    expect(human.code).toBe(0);
    expect(human.stdout).toStartWith('v3 · sha256:ab · commit 0123456 · ');
    expect(human.stdout).toContain('triggers: none (manual only)');
    expect(
      (await pl(['pipeline', 'manifest', 'alpha', '--json'], { fetch })).stdout,
    ).toBe(manifest + '\n');
    const effective = await pl(['pipeline', 'manifest', 'alpha', '--effective'], {
      fetch,
    });
    expect(effective.stdout).toContain('effective with config #2: target = ssh');
    await pl(['pipeline', 'manifest', 'alpha', '--version', 'v3'], { fetch });
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/versions/3`);
  });

  test('a pipeline without an active version says so', async () => {
    const { fetch } = fakeFetch((req) =>
      req.path === '/api/pipelines'
        ? { body: [alpha()] }
        : { body: { activation: null, versions: [] } },
    );
    expect((await pl(['pipeline', 'manifest', 'alpha'], { fetch })).stderr).toContain(
      "Pipeline 'alpha' has no active version yet.",
    );
  });
});

describe('pipeline reliability', () => {
  const step = (overrides: Record<string, unknown> = {}) => ({
    step: 'test',
    attempts: 40,
    failures: 6,
    ownFailures: 4,
    infrastructureFailures: 1,
    timeouts: 1,
    failureRate: 0.15,
    recentFailureRate: 0.4,
    usualFailureRate: 0.1,
    aboveUsual: true,
    flakyFailures: 2,
    reproducedFailures: 1,
    unverifiedFailures: 1,
    flakyRate: 0.05,
    flakyRuns: [70, 64],
    ...overrides,
  });
  const report = (overrides: Record<string, unknown> = {}) => ({
    window: {
      fromRun: 12,
      toRun: 71,
      runs: 60,
      requestedRuns: 100,
      from: '2026-10-01T10:00:00Z',
      to: '2026-10-28T10:00:00Z',
      limitedByRetention: true,
    },
    runs: {
      settled: 50,
      failedForNoReason: 2,
      rate: 0.04,
      absorbed: 1,
      runNumbers: [70, 64],
    },
    steps: [step()],
    notes: [],
    ...overrides,
  });
  const fake = (body: unknown) =>
    fakeFetch((req) =>
      req.path.includes('/reliability') ? { body } : { body: [alpha()] },
    );

  test('states the window, the no-reason rate and each step', async () => {
    const { fetch, requests } = fake(report());
    const result = await pl(['pipeline', 'reliability', 'alpha', '--runs', '100'], {
      fetch,
    });
    expect(result.code).toBe(0);
    expect(result.stdout).toContain('Covers runs #12..#71 (60 of the latest 100');
    expect(result.stdout).toContain('older runs were removed by retention');
    expect(result.stdout).toContain(
      'Failed for no real reason: 2 of 50 settled runs (4.0%): #70, #64; retries absorbed 1 more.',
    );
    expect(result.stdout).toContain('above usual (40.0% vs 10.0%)');
    expect(result.stdout).toMatch(/test\s+40\s+6\s+15\.0%\s+4\s+1\s+1\s+2\s+1\s+1/);
    expect(requests.at(-1)!.path).toEndWith(`/api/pipelines/${ID}/reliability?runs=100`);
  });

  test('an empty pipeline says so, with what the report could not work out', async () => {
    const empty = report({
      window: {
        fromRun: 0,
        toRun: 0,
        runs: 0,
        requestedRuns: 100,
        from: null,
        to: null,
        limitedByRetention: false,
      },
      runs: { settled: 0, failedForNoReason: 0, rate: 0, absorbed: 0, runNumbers: [] },
      steps: [],
      notes: ["The plan of version 3 can't be read"],
    });
    const { fetch } = fake(empty);
    const result = await pl(['pipeline', 'reliability', 'alpha'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toContain('Covers no runs yet.');
    expect(result.stdout).toContain('No settled runs yet.');
    expect(result.stdout).toContain("The plan of version 3 can't be read");
  });

  test('--json emits the report; --runs must be a number', async () => {
    const { fetch } = fake(report());
    const json = await pl(['pipeline', 'reliability', 'alpha', '--json'], { fetch });
    expect(JSON.parse(json.stdout).steps[0].flakyRuns).toEqual([70, 64]);

    const bad = await pl(['pipeline', 'reliability', 'alpha', '--runs', 'many'], {
      fetch,
    });
    expect(bad.code).not.toBe(0);
    expect(bad.stderr).toContain('--runs must be a number.');
  });
});
