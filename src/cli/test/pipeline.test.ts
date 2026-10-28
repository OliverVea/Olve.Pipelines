import { describe, expect, test } from 'bun:test';
import { ID, fakeFetch, health, pl } from './helpers';

const alpha = (overrides: Record<string, unknown> = {}) => ({
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
            lastRun: { number: 41, status: 'Failed', createdAt: '2026-10-28T10:00:00Z' },
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
