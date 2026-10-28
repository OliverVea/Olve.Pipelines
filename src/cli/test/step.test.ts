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

const attempt = (number: number, type: string, extra: Record<string, unknown> = {}) => ({
  id: ID,
  pipelineId: ID,
  runId: ID,
  runNumber: 15,
  stepName: 'build',
  number,
  status: { type, ...extra },
  createdAt: '2026-10-28T10:00:00Z',
});

describe('step', () => {
  test('logs --follow prints the log as it grows and stops when the attempt finishes', async () => {
    let polls = 0;
    const { fetch } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path.endsWith('/attempts')) {
        polls++;
        return {
          body: [attempt(1, polls < 3 ? 'in-progress' : 'succeeded', { startedAt: '' })],
        };
      }
      if (req.path.includes('/logs'))
        return { body: JSON.stringify(polls < 2 ? 'a\n' : 'a\nb\n') };
      return { status: 404 };
    });
    const result = await pl(['step', 'logs', 'alpha#15/build', '--follow'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toBe('a\nb\n');
  });

  test('rerun passes --continue and rejects an attempt in the address', async () => {
    const { fetch, requests } = fakeFetch((req) =>
      req.path === '/api/pipelines'
        ? { body: [alpha] }
        : { body: attempt(2, 'scheduled', { submissionAttempts: 0 }) },
    );
    const result = await pl(['step', 'rerun', 'alpha#15/build', '--continue'], { fetch });
    expect(result.code).toBe(0);
    expect(requests.at(-1)!.body).toEqual({ continue: true });
    expect(result.stdout).toBe(
      'alpha#15/build@2  scheduled (run continues downstream)\n',
    );
    const bad = await pl(['step', 'rerun', 'alpha#15/build@1'], { fetch });
    expect(bad.code).toBe(1);
    expect(bad.stderr).toContain('drop the @1');
  });

  test('block and unblock print the blocked list', async () => {
    const { fetch, requests } = fakeFetch((req) =>
      req.path === '/api/pipelines' ? { body: [alpha] } : { body: ['deploy-prod'] },
    );
    const result = await pl(['step', 'block', 'alpha/deploy-prod'], { fetch });
    expect(result.code).toBe(0);
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/steps/deploy-prod/block`);
    expect(result.stdout).toBe('blocked alpha/deploy-prod\nblocked: deploy-prod\n');
  });

  test('file downloads to stdout or to a file, and needs a path once', async () => {
    const { fetch, requests } = fakeFetch((req) =>
      req.path === '/api/pipelines' ? { body: [alpha] } : { body: 'bytes!' },
    );
    const stdout = await pl(['step', 'file', 'alpha#15/build@1', 'dist/a.txt'], {
      fetch,
    });
    expect(stdout.code).toBe(0);
    expect(stdout.stdout).toBe('bytes!');
    expect(requests.at(-1)!.path).toBe(
      `/api/pipelines/${ID}/runs/15/steps/build/files/dist/a.txt?attempt=1`,
    );
    const twice = await pl(['step', 'file', 'alpha#15/build/dist/a.txt', 'b.txt'], {
      fetch,
    });
    expect(twice.code).toBe(1);
    expect(twice.stderr).toContain('Give the file path once');
  });

  test('attempts and input list render addresses', async () => {
    const { fetch } = fakeFetch(() => ({
      body: [
        {
          pipelineId: ID,
          pipelineName: 'alpha',
          run: 15,
          step: 'build',
          attempt: 2,
          status: 'in-progress',
          createdAt: '2026-10-28T10:00:00Z',
          startedAt: null,
        },
      ],
    }));
    const result = await pl(['attempts'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toContain('alpha#15/build@2  in-progress');
    const waiting = await pl(['input', 'list'], { fetch });
    expect(waiting.stdout).toContain('alpha#15/build');
  });
});
