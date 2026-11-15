import { describe, expect, test } from 'vitest';
import type {
  CiHealth,
  CiRun,
  CiRunSummary,
  Pull,
  QueueEntry,
  RunStatus,
} from '@olve/api';
import {
  ciManifest,
  ciTone,
  pullRun,
  pullStatus,
  previewCell,
  pullTags,
  pullsCell,
  queuePill,
  queueWhy,
  runWhy,
  staleTitle,
  stripOf,
} from '../src/model/ci.js';
import { matrixOf } from '../src/model/matrix.js';
import type { StepsStage, WhenStage } from '../src/model/manifest.js';

const run = (over: Partial<CiRunSummary> = {}): CiRunSummary => ({
  id: 'r',
  number: 57,
  kind: 'pull',
  status: 'Running',
  pulls: [148],
  testedSha: 'f1e2d3c4b5',
  tree: 't',
  mainSha: '1a2b3c4d5e',
  createdAt: '2026-11-15T10:00:00Z',
  finishedAt: null,
  supersededByNumber: null,
  cancelReason: null,
  steps: [],
  ...over,
});

const pull = (over: Partial<Pull> = {}): Pull => ({
  number: 148,
  title: 'feat(runs): a matrix cell reruns on its own',
  branch: 'OliverVea/matrix-retry',
  headSha: 'a1b2c3d',
  draft: false,
  state: 'open',
  conflict: false,
  stale: false,
  latestRun: run(),
  queue: null,
  preview: null,
  updatedAt: '2026-11-15T10:00:00Z',
  ...over,
});

const entry = (over: Partial<QueueEntry> = {}): QueueEntry => ({
  pull: 147,
  title: 'fix(sync): reconcile a binding whose path moved',
  branch: 'OliverVea/binding-path',
  headSha: 'e4f5a6b',
  state: 'queued',
  position: 1,
  enqueuedAt: '2026-11-15T10:00:00Z',
  via: 'api',
  enqueuedBy: 'OliverVea',
  runs: [],
  finishedAt: null,
  reason: null,
  failedStep: null,
  ...over,
});

describe('a pull request row', () => {
  test.each([
    ['its latest run', pull(), 'Running', 57],
    ['conflict, with no run shown', pull({ conflict: true }), 'conflict', null],
    ['pending before its first run', pull({ latestRun: null }), 'pending', null],
    [
      'merged before its first run',
      pull({ latestRun: null, state: 'merged' }),
      'Cancelled',
      null,
    ],
    [
      'closed before its first run',
      pull({ latestRun: null, state: 'closed' }),
      'Cancelled',
      null,
    ],
  ] as [string, Pull, string, number | null][])('%s', (_, p, status, number) => {
    expect(pullStatus(p)).toBe(status);
    expect(pullRun(p)?.number ?? null).toBe(number);
  });

  test.each([
    ['not queued', pull(), null],
    ['queued', pull({ queue: entry({ position: 2 }) }), 'queue 2'],
    [
      'merged, out of the queue',
      pull({ queue: entry({ state: 'merged', position: null }) }),
      null,
    ],
  ] as [string, Pull, string | null][])('queue pill: %s', (_, p, pill) =>
    expect(queuePill(p)).toBe(pill),
  );

  test('the stale tag says which main the run was tested on, and main now', () =>
    expect(staleTitle(run(), '5d6e7f8aaa')).toBe(
      'tested on main 1a2b3c4; main is now 5d6e7f8',
    ));
});

test.each([
  ['conflict', 'wait'],
  ['queued', 'idle'],
  ['testing', 'run'],
  ['merged', 'ok'],
  ['removed', 'fail'],
  ['Succeeded', 'ok'],
  ['Failed', 'fail'],
  ['Superseded', 'off'],
  ['pending', 'idle'],
])('%s is shown as %s', (status, tone) => expect(ciTone(status)).toBe(tone));

test('the step strip groups steps by stage, in stage order', () =>
  expect(
    stripOf([
      { name: 'b', stage: 1, status: 'succeeded' },
      { name: 'a', stage: 0, status: 'succeeded' },
      { name: 'c', stage: 1, status: 'failed' },
    ]).map((s) => s.map((x) => x.name)),
  ).toEqual([['a'], ['b', 'c']]));

describe('why a run ended', () => {
  test.each([
    ['superseded', run({ supersededByNumber: 58 }), { supersededBy: 58 }],
    ['closed', run({ cancelReason: 'closed' }), { text: 'pull request closed' }],
    ['merged', run({ cancelReason: 'merged' }), { text: 'pull request merged' }],
    ['dequeued', run({ cancelReason: 'dequeued' }), { text: 'dequeued' }],
    ['nothing to say', run(), null],
  ] as [string, CiRunSummary, unknown][])('%s', (_, r, why) =>
    expect(runWhy(r)).toEqual(why),
  );
});

describe('a queue entry note', () => {
  const statuses: Record<number, RunStatus> = { 59: 'Failed', 60: 'Superseded' };
  test.each([
    [
      'removed: its reason',
      entry({ state: 'removed', reason: 'test failed', runs: [61] }),
      { text: 'test failed' },
    ],
    ['queued again after a failed batch split', entry({ runs: [59] }), { split: 59 }],
    ['queued again after main moved: nothing', entry({ runs: [60] }), null],
    ['queued, never tested', entry(), null],
    ['testing', entry({ state: 'testing', runs: [60] }), null],
  ] as [string, QueueEntry, unknown][])('%s', (_, e, why) =>
    expect(queueWhy(e, (n) => statuses[n])).toEqual(why),
  );
});

describe('pull requests on the overview and the homepage', () => {
  const ci = (over: Partial<CiHealth>): CiHealth => ({
    open: 5,
    failing: 0,
    running: 0,
    queued: 0,
    ...over,
  });
  test.each([
    ['no CI', null, null],
    ['no open pull requests', ci({ open: 0 }), null],
    ['all passing', ci({}), { tone: 'ok', value: '5 open', sub: 'all passing' }],
    ['running', ci({ running: 2 }), { tone: 'run', value: '5 open', sub: '2 running' }],
    [
      'the worst status wins',
      ci({ failing: 1, running: 2 }),
      { tone: 'fail', value: '5 open', sub: '1 failing · 2 running' },
    ],
  ] as [string, CiHealth | null, unknown][])('cell: %s', (_, h, cell) =>
    expect(pullsCell(h)).toEqual(cell),
  );

  test.each([
    ['quiet while all pass', ci({}), {}],
    ['one failing', ci({ failing: 1 }), { failing: '1 PR failing' }],
    ['several running', ci({ running: 2 }), { running: '2 PRs running' }],
  ] as [string, CiHealth, unknown][])('tags: %s', (_, h, tags) =>
    expect(pullTags(h)).toEqual(tags),
  );
});

describe('the stage graph of a CI run', () => {
  const ciRun = (stages: CiRun['stages']): CiRun => ({
    ...run(),
    pullHeads: [],
    definitionSha: 'x',
    stages,
    problems: [],
  });
  const step = (name: string, cells?: Record<string, string>[]) => ({
    name,
    status: 'succeeded' as const,
    reason: null,
    attemptCount: 1,
    latestAttempt: 1,
    succeededAttempt: 1,
    startedAt: null,
    finishedAt: null,
    reusedFrom: null,
    cells: cells?.map((cell) => ({
      name: `${name}[${Object.entries(cell)
        .map(([k, v]) => `${k}=${v}`)
        .join(',')}]`,
      cell,
      status: 'succeeded' as const,
      reason: null,
      attemptCount: 1,
      latestAttempt: 1,
      succeededAttempt: 1,
      startedAt: null,
      finishedAt: null,
      reusedFrom: null,
    })),
  });

  test('stages keep their order and their when', () => {
    const m = ciManifest(
      ciRun([
        { index: 0, when: [], steps: [step('source')] },
        { index: 1, when: [{ field: 'kind', values: ['full'] }], steps: [step('test')] },
      ]),
    );
    expect((m.stages[0] as StepsStage).steps.map((s) => s.name)).toEqual(['source']);
    expect((m.stages[1] as WhenStage).when).toEqual({ kind: ['full'] });
  });

  test("a matrix step's cells are exactly the ones that ran", () => {
    const ran = [
      { os: 'linux', arch: 'x64' },
      { os: 'windows', arch: 'x64' },
      { os: 'linux', arch: 'arm64' },
    ];
    const m = ciManifest(
      ciRun([{ index: 0, when: [], steps: [step('build-cli', ran)] }]),
    );
    expect(matrixOf((m.stages[0] as StepsStage).steps[0])?.cells).toEqual([
      { os: 'linux', arch: 'x64' },
      { os: 'linux', arch: 'arm64' },
      { os: 'windows', arch: 'x64' },
    ]);
  });
});

describe('a pull request preview', () => {
  const preview = (state: string, links: { url: string; label?: string | null }[] = []) =>
    pull({
      preview: {
        state: state as any,
        links,
        deployedBy: 57,
        deployedAt: '2026-11-15T10:00:00Z',
      },
    });
  test.each([
    ['none', pull(), null],
    ['deploying', preview('deploying'), { tone: 'run', label: 'deploying', links: [] }],
    [
      'live with one link: its host',
      preview('live', [{ url: 'https://pr-58.trains-preview.ovea.pro/' }]),
      {
        tone: 'ok',
        label: null,
        links: [
          {
            href: 'https://pr-58.trains-preview.ovea.pro/',
            text: 'pr-58.trains-preview.ovea.pro',
          },
        ],
      },
    ],
    [
      'live with several: by label, else host',
      preview('live', [
        { url: 'https://web.x', label: 'web' },
        { url: 'https://api.x', label: null },
      ]),
      {
        tone: 'ok',
        label: null,
        links: [
          { href: 'https://web.x', text: 'web' },
          { href: 'https://api.x', text: 'api.x' },
        ],
      },
    ],
    ['live without links', preview('live'), { tone: 'ok', label: 'live', links: [] }],
    ['removing', preview('removing'), { tone: 'run', label: 'removing', links: [] }],
    ['failing', preview('failing'), { tone: 'fail', label: 'failing', links: [] }],
    ['removed', preview('removed'), { tone: 'off', label: 'removed', links: [] }],
  ] as const)('%s', (_, p, cell) => {
    expect(previewCell(p)).toEqual(cell);
  });
});
