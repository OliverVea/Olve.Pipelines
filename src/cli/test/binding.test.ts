import type { PipelineBinding, PipelineBindingStatus } from '@olve/api';
import { describe, expect, test } from 'bun:test';
import { ID, fakeFetch, health, pl } from './helpers';

const BINDING_ID = '22222222-2222-2222-2222-222222222222';
const SHA = 'a'.repeat(40);
const OTHER = 'b'.repeat(40);

const alpha = {
  id: ID,
  name: 'alpha',
  description: null,
  repo: null,
  health: health(),
  stages: [],
};

const bound: PipelineBinding = {
  id: BINDING_ID,
  pipelineId: ID,
  repo: 'OliverVea/app',
  branch: 'main',
  path: '.pipelines',
  credentialsSecret: 'GITHUB_TOKEN',
  lastPushSha: SHA,
  lastReconciledSha: OTHER,
  head: null,
  status: {
    result: 'Error',
    lastSyncTime: '2026-10-28T11:59:30.123Z',
    problems: ['[E1] stages[0]: broken'],
    warnings: ['[W] stages: unused output'],
    sha: OTHER,
    inProgress: true,
    versionNumber: 7,
  },
  createdAt: '2026-10-01T00:00:00Z',
  polls: false,
};

const fresh: PipelineBinding = {
  ...bound,
  credentialsSecret: null,
  lastPushSha: null,
  lastReconciledSha: null,
  status: {
    result: 'NeverRun',
    lastSyncTime: null,
    problems: [],
    warnings: [],
    sha: null,
    inProgress: false,
    versionNumber: null,
  },
  polls: true,
};

const api = (body: unknown) =>
  fakeFetch((req) => (req.path === '/api/pipelines' ? { body: [alpha] } : { body }));

describe('binding get', () => {
  test.each([
    [
      'every field',
      bound,
      [
        `Binding:     ${BINDING_ID}`,
        `Pipeline:    ${ID}`,
        'Repo:        OliverVea/app@main (.pipelines)',
        'Credentials: GITHUB_TOKEN',
        'Polling:     off (syncs only on reconcile or its webhook)',
        `Last push:   ${SHA}`,
        `Reconciled:  ${OTHER}`,
        'Reconcile:   Error (last sync 2026-10-28 11:59:30Z)',
        `  commit:    ${OTHER} (in progress)`,
        '  version:   v7',
        '  problem: [E1] stages[0]: broken',
        '  warning: [W] stages: unused output',
      ],
    ],
    [
      'a binding that never reconciled',
      fresh,
      [
        `Binding:     ${BINDING_ID}`,
        `Pipeline:    ${ID}`,
        'Repo:        OliverVea/app@main (.pipelines)',
        'Credentials: (none)',
        'Last push:   (none)',
        'Reconciled:  (none)',
        'Reconcile:   NeverRun',
        '  commit:    (none)',
        '  version:   (none)',
      ],
    ],
  ])('prints %s', async (_, binding, lines) => {
    const { fetch, requests } = api(binding);

    const out = await pl(['binding', 'get', 'alpha'], { fetch });

    expect(out.code).toBe(0);
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/binding`);
    expect(out.stdout).toBe(lines.map((l) => `${l}\n`).join(''));
  });
});

describe('binding status', () => {
  const status: PipelineBindingStatus = {
    pipelineId: ID,
    repo: 'OliverVea/app',
    branch: 'main',
    path: '.pipelines',
    lastPushSha: SHA,
    lastReconciledSha: SHA,
    head: null,
    result: 'Success',
    lastSyncTime: '2026-10-28T11:59:30Z',
    problems: [],
    warnings: [],
    reconcileSha: SHA,
    reconcileInProgress: false,
    reconciledVersion: 8,
    activation: {
      type: 'active',
      versionId: '33333333-3333-3333-3333-333333333333',
      versionNumber: 7,
      since: '2026-10-28T10:00:00Z',
    },
    pending: null,
  };

  test.each([
    [
      'an applied commit',
      status,
      [
        `Pipeline:    ${ID}`,
        'Repo:        OliverVea/app@main (.pipelines)',
        'Reconcile:   Success (last sync 2026-10-28 11:59:30Z)',
        `  commit:    ${SHA}`,
        '  version:   v8',
        'Active:      active v7',
        `Last push:   ${SHA}`,
        `Reconciled:  ${SHA}`,
      ],
    ],
    [
      'a pending version, problems and warnings',
      {
        ...status,
        result: 'Error' as const,
        lastSyncTime: null,
        reconcileInProgress: true,
        reconciledVersion: null,
        activation: null,
        problems: ['[E1] broken', '[E2] also broken'],
        warnings: ['[W] unused'],
        pending: {
          version: 9,
          since: '2026-10-28T11:00:00Z',
          held: false,
          reasons: ["secret 'X' is not set"],
          changedDefaults: [],
        },
      },
      [
        `Pipeline:    ${ID}`,
        'Repo:        OliverVea/app@main (.pipelines)',
        'Reconcile:   Error',
        `  commit:    ${SHA} (in progress)`,
        '  version:   (none)',
        'Active:      (no version)',
        "Pending:     pending v9: secret 'X' is not set",
        `Last push:   ${SHA}`,
        `Reconciled:  ${SHA}`,
        'Problems:',
        '  - [E1] broken',
        '  - [E2] also broken',
        'Warnings:',
        '  - [W] unused',
      ],
    ],
  ])('prints %s', async (_, body, lines) => {
    const { fetch, requests } = api(body);

    const out = await pl(['binding', 'status', 'alpha'], { fetch });

    expect(out.code).toBe(0);
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/binding/status`);
    expect(out.stdout).toBe(lines.map((l) => `${l}\n`).join(''));
  });
});

describe('binding changes', () => {
  test.each([
    [
      ['set-credentials', 'alpha', 'GITHUB_TOKEN'],
      'PATCH',
      '/binding',
      { credentialsSecret: 'GITHUB_TOKEN' },
    ],
    [['set-credentials', 'alpha'], 'PATCH', '/binding', { credentialsSecret: null }],
    [['set-branch', 'alpha', 'release'], 'PUT', '/binding/branch', { branch: 'release' }],
    [['set-polling', 'alpha', 'on'], 'PUT', '/binding/polling', { polls: true }],
    [['set-polling', 'alpha', 'off'], 'PUT', '/binding/polling', { polls: false }],
  ])('%p sends %s %s', async (argv, method, path, body) => {
    const { fetch, requests } = api(fresh);

    const out = await pl(['binding', ...argv], { fetch });

    expect(out.code).toBe(0);
    expect(requests.at(-1)!.method).toBe(method);
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}${path}`);
    expect(requests.at(-1)!.body).toEqual(body);
    expect(out.stdout).toStartWith(`Binding:     ${BINDING_ID}\n`);
  });

  test('set-polling refuses anything but on or off before calling the server', async () => {
    const { fetch, requests } = api(fresh);

    const out = await pl(['binding', 'set-polling', 'alpha', 'yes'], { fetch });

    expect(out.code).toBe(1);
    expect(out.stderr).toContain("Polling must be on or off, got 'yes'.");
    expect(requests).toEqual([]);
  });
});
