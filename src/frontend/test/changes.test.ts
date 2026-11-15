import { describe, expect, test } from 'vitest';
import type { RunChanges } from '@olve/api';
import { commitRuns, hasChanges } from '../src/model/changes.js';

const since = (over: Partial<RunChanges> = {}): RunChanges => ({
  number: 44,
  sinceNumber: 41,
  sinceVersionNumber: 13,
  sinceConfigSnapshot: 5,
  commits: [],
  totalCommits: 0,
  compareUrl: null,
  commitsError: null,
  ...over,
});
const commit = { sha: 'a6d2fbc', title: 't', author: 'a', committedAt: '', url: '' };

describe('hasChanges', () => {
  test.each([
    ['no older run succeeded', since({ sinceNumber: null }), false],
    ['the same commit, version and config', since(), false],
    ['commits', since({ commits: [commit], totalCommits: 1 }), true],
    ['a newer version', since({ sinceVersionNumber: 12 }), true],
    ['newer config', since({ sinceConfigSnapshot: 4 }), true],
    [
      "commits GitHub couldn't list",
      since({ commits: null, commitsError: 'GitHub returned 502.' }),
      true,
    ],
  ] as [string, RunChanges, boolean][])('%s', (_, changes, expected) =>
    expect(hasChanges(changes, 13, 5)).toBe(expected),
  );

  test('nothing loaded yet', () => expect(hasChanges(null, 13, 5)).toBe(false));
});

describe('commitRuns', () => {
  const commits = ['fadfcb', 'facbee', 'ffffff', 'aaaaaa', 'bbbbbb'].map((sha) => ({
    sha,
  }));

  test.each([
    [
      'each run on the commit it pushed',
      [
        { number: 189, pushSha: 'fadfcb' },
        { number: 188, pushSha: 'ffffff' },
        { number: 186, pushSha: 'aaaaaa' },
      ],
      [189, null, 188, 186, null],
    ],
    [
      'runs outside the range and runs without a push',
      [
        { number: 190, pushSha: 'facbee' },
        { number: 189, pushSha: 'fadfcb' },
        { number: 187, pushSha: null },
        { number: 185, pushSha: 'bbbbbb' },
      ],
      [189, null, null, null, null],
    ],
    [
      'the newest of runs that pushed the same commit',
      [
        { number: 189, pushSha: 'fadfcb' },
        { number: 188, pushSha: 'FFFFFF' },
        { number: 187, pushSha: 'ffffff' },
      ],
      [189, null, 188, null, null],
    ],
  ] as [string, { number: number; pushSha: string | null }[], (number | null)[]][])(
    '%s',
    (_, runs, expected) => expect(commitRuns(commits, runs, 185, 189)).toEqual(expected),
  );
});
