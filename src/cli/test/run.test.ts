import { describe, expect, test } from 'bun:test';
import { coerce } from '../src/input';
import { ID, fakeFetch, health, pl } from './helpers';

const alpha = {
  id: ID,
  name: 'alpha',
  description: null,
  repo: null,
  health: health(),
  stages: [],
};

const manifest = JSON.stringify({
  input: 'Input',
  models: {
    Input: {
      input: {
        type: 'object',
        properties: {
          sha: { type: 'string' },
          mode: { enum: ['normal', 'skip-last'] },
          count: { type: 'integer' },
          dry: { type: 'boolean' },
        },
        required: ['sha'],
      },
    },
  },
  stages: [
    { steps: [{ name: 'build' }] },
    { steps: [{ name: 'approve', externalInputs: { decision: { model: 'Approval' } } }] },
  ],
});

const run = (overrides: Record<string, unknown> = {}) => ({
  id: ID,
  number: 15,
  status: 'Running',
  triggerKind: 'Manual',
  versionNumber: 4,
  versionStamp: '2026.10.28.15',
  createdAt: '2026-10-28T10:00:00Z',
  finishedAt: null,
  heldAt: null,
  supersededByNumber: null,
  rerunOfNumber: null,
  configSnapshot: 13,
  versionId: ID,
  commitSha: 'dbdb4b02dc1f',
  input: { mode: 'normal', sha: '1234567' },
  continuable: true,
  stages: [
    {
      index: 0,
      when: [],
      steps: [
        {
          name: 'hello',
          stepVersion: 2,
          status: 'succeeded',
          reason: 'boom',
          attemptCount: 2,
          latestAttempt: 2,
          succeededAttempt: 1,
          startedAt: '2026-10-28T10:00:00Z',
          finishedAt: '2026-10-28T10:01:05Z',
          blocked: false,
        },
      ],
    },
    {
      index: 1,
      when: [{ field: 'mode', values: ['normal'] }],
      steps: [
        {
          name: 'last',
          stepVersion: null,
          status: 'held',
          reason: null,
          attemptCount: 0,
          latestAttempt: null,
          succeededAttempt: null,
          startedAt: null,
          finishedAt: null,
          blocked: true,
        },
      ],
    },
  ],
  config: null,
  ...overrides,
});

describe('trigger', () => {
  test('types --field values by the active version and prints the address', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `/api/pipelines/${ID}/versions`)
        return {
          body: {
            activation: { type: 'active', versionId: ID, versionNumber: 4, since: '' },
            versions: [],
          },
        };
      if (req.path === `/api/pipelines/${ID}/versions/4`)
        return {
          body: {
            id: ID,
            number: 4,
            manifest,
            manifestHash: '',
            commitSha: null,
            createdAt: '',
            stepVersions: {},
            modelVersions: {},
          },
        };
      if (req.method === 'POST') return { body: { outcome: 'started', run: run() } };
      return { status: 404 };
    });
    const result = await pl(
      [
        'trigger',
        'alpha',
        '--field',
        'sha=1234567',
        '--field',
        'count=3',
        '--field',
        'dry=true',
        '--field',
        'mode=skip-last',
      ],
      { fetch },
    );
    expect(result.code).toBe(0);
    expect(requests.at(-1)!.body).toEqual({
      sha: '1234567',
      count: 3,
      dry: true,
      mode: 'skip-last',
    });
    expect(result.stdout.split('\n')[0]).toBe('alpha#15');
    expect(result.stdout).toContain('started run #15 on v4 (2026.10.28.15)');
  });

  test('a queued start says so; a bad field is a problem before anything is sent', async () => {
    const { fetch, requests } = fakeFetch((req) =>
      req.path === '/api/pipelines'
        ? { body: [alpha] }
        : { body: { outcome: 'queued', run: null } },
    );
    const queued = await pl(['trigger', ID, '--input', '{"sha":"abc"}'], { fetch });
    expect(queued.code).toBe(0);
    expect(queued.stdout).toContain('queued');
    expect(requests.at(-1)!.body).toEqual({ sha: 'abc' });
    const bad = await pl(['trigger', ID, '--field', 'nonsense'], { fetch });
    expect(bad.code).toBe(1);
    expect(bad.stderr).toContain('--field expects key=value');
  });

  test('coercion follows the schema', () => {
    expect(coerce('n', '42', { type: 'integer' })).toBe(42);
    expect(() => coerce('n', 'x', { type: 'integer' })).toThrow('is an integer');
    expect(coerce('s', '0123', { type: 'string' })).toBe('0123');
    expect(coerce('s', '0123', undefined)).toBe('0123');
    expect(coerce('s', '123', undefined)).toBe(123);
    expect(coerce('s', 'abc', undefined)).toBe('abc');
    expect(coerce('o', '{"a":1}', { type: 'object' })).toEqual({ a: 1 });
    expect(() => coerce('b', 'yes', { type: 'boolean' })).toThrow('b=true or b=false');
  });
});

describe('run get and list', () => {
  test('get renders the run and its stages', async () => {
    const { fetch } = fakeFetch((req) =>
      req.path === '/api/pipelines' ? { body: [alpha] } : { body: run() },
    );
    const result = await pl(['run', 'get', 'alpha#15'], { fetch });
    expect(result.code).toBe(0);
    expect(result.stdout).toContain('alpha#15  Running');
    expect(result.stdout).toContain(
      'version:   v4, config #13 · 2026.10.28.15 · commit dbdb4b0',
    );
    expect(result.stdout).toContain('input:     {"mode":"normal","sha":"1234567"}');
    expect(result.stdout).toContain('stage 2  when mode in ["normal"]');
    expect(result.stdout).toContain('@2/2');
    expect(result.stdout).toContain('uses @1; boom');
    expect(result.stdout).toContain('blocked');
  });

  test('list pages with offset and limit', async () => {
    const { fetch, requests } = fakeFetch((req) =>
      req.path === '/api/pipelines'
        ? { body: [alpha] }
        : { body: { items: [run()], offset: 20, limit: 20, total: 41 } },
    );
    const result = await pl(['run', 'list', 'alpha', '--page', '1'], { fetch });
    expect(result.code).toBe(0);
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/runs?offset=20&limit=20`);
    expect(result.stdout).toContain('#15');
    expect(result.stdout).toContain('page 1 · 1 of 41 run(s)');
  });

  test('continue sends only the changed fields, typed by the pinned version', async () => {
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `/api/pipelines/${ID}/versions/4`)
        return {
          body: {
            id: ID,
            number: 4,
            manifest,
            manifestHash: '',
            commitSha: null,
            createdAt: '',
            stepVersions: {},
            modelVersions: {},
          },
        };
      return { body: run({ input: { mode: 'skip-last' } }) };
    });
    const result = await pl(
      ['run', 'continue', 'alpha#15', '--field', 'mode=skip-last'],
      { fetch },
    );
    expect(result.code).toBe(0);
    const post = requests.find((r) => r.method === 'POST')!;
    expect(post.path).toBe(`/api/pipelines/${ID}/runs/15/continue`);
    expect(post.body).toEqual({ input: { mode: 'skip-last' } });
    expect(result.stdout).toContain('alpha#15  Running (input {"mode":"skip-last"})');
    const none = await pl(['run', 'continue', 'alpha#15'], { fetch });
    expect(requests.at(-1)!.body).toEqual({});
    expect(none.code).toBe(0);
  });
});

describe('input answer', () => {
  test('types the answer by the external input model of the pinned manifest', async () => {
    const withApproval = JSON.parse(manifest);
    withApproval.models.Approval = {
      input: {
        type: 'object',
        properties: { approved: { type: 'boolean' }, comment: { type: 'string' } },
      },
    };
    const { fetch, requests } = fakeFetch((req) => {
      if (req.path === '/api/pipelines') return { body: [alpha] };
      if (req.path === `/api/pipelines/${ID}/runs/15`) return { body: run() };
      if (req.path === `/api/pipelines/${ID}/versions/4`)
        return {
          body: {
            id: ID,
            number: 4,
            manifest: JSON.stringify(withApproval),
            manifestHash: '',
            commitSha: null,
            createdAt: '',
            stepVersions: {},
            modelVersions: {},
          },
        };
      return {
        body: {
          id: ID,
          pipelineId: ID,
          runId: ID,
          runNumber: 15,
          stepName: 'approve',
          number: 1,
          status: { type: 'scheduled', submissionAttempts: 0 },
          createdAt: '',
        },
      };
    });
    const result = await pl(
      [
        'input',
        'answer',
        'alpha#15/approve',
        '--field',
        'approved=true',
        '--field',
        'comment=ok',
      ],
      { fetch },
    );
    expect(result.code).toBe(0);
    const post = requests.find((r) => r.method === 'POST')!;
    expect(post.path).toBe(`/api/pipelines/${ID}/runs/15/steps/approve/input`);
    expect(post.body).toEqual({ name: null, value: { approved: true, comment: 'ok' } });
    expect(result.stdout).toBe('alpha#15/approve@1  scheduled\n');
  });
});
