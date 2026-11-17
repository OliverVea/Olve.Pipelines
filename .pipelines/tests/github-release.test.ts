import { describe, expect, test } from 'bun:test';
import type { Ctx } from '@olivervea/pipelines';
import { githubRelease, releaseNotes } from '../github-release';

const commit = (sha: string, message: string) => ({
  sha,
  html_url: `https://github.com/o/r/commit/${sha}`,
  commit: { message },
});
const compareUrl = 'https://github.com/o/r/compare/a...b';

describe('release notes', () => {
  test.each([
    [
      'grouped by type in a fixed order, scope kept, body dropped',
      [
        commit('1111111aaa', 'docs: a guide'),
        commit('2222222aaa', 'feat(ci): poll pull requests\n\nLong body.'),
        commit('3333333aaa', 'fix: a crash'),
        commit('4444444aaa', 'feat: a chart'),
      ],
      [
        '## Changes',
        '### Features\n\n- ci: poll pull requests ([2222222](https://github.com/o/r/commit/2222222aaa))\n- a chart ([4444444](https://github.com/o/r/commit/4444444aaa))',
        '### Fixes\n\n- a crash ([3333333](https://github.com/o/r/commit/3333333aaa))',
        '### Docs\n\n- a guide ([1111111](https://github.com/o/r/commit/1111111aaa))',
        `[All changes since v1](${compareUrl})`,
      ],
    ],
    [
      'chore, test, ci, build and style are left out; a breaking one of any type is not',
      [
        commit('1111111aaa', 'chore: bump'),
        commit('2222222aaa', 'test: a row'),
        commit('3333333aaa', 'ci: x'),
        commit('4444444aaa', 'chore(deps)!: drop Node 20'),
      ],
      [
        '## Changes',
        '### Breaking changes\n\n- deps: drop Node 20 ([4444444](https://github.com/o/r/commit/4444444aaa))',
        `[All changes since v1](${compareUrl})`,
      ],
    ],
    [
      'unknown types and plain subjects are Other',
      [commit('1111111aaa', 'wip: x'), commit('2222222aaa', 'Merge things')],
      [
        '## Changes',
        '### Other\n\n- x ([1111111](https://github.com/o/r/commit/1111111aaa))\n- Merge things ([2222222](https://github.com/o/r/commit/2222222aaa))',
        `[All changes since v1](${compareUrl})`,
      ],
    ],
    [
      'only hidden commits',
      [commit('1111111aaa', 'test: a row')],
      ['## Changes', 'Nothing user-facing.', `[All changes since v1](${compareUrl})`],
    ],
  ])('%s', (_, commits, sections) => {
    expect(releaseNotes(commits, 'v1', compareUrl)).toBe(sections.join('\n\n'));
  });
});

function github(releases: object[], existing?: object) {
  const calls: { method: string; url: string; body?: unknown }[] = [];
  const json = (status: number, body: unknown) =>
    new Response(JSON.stringify(body), { status });
  const fetch = async (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    calls.push({
      method,
      url,
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
    });
    if (url.includes('/releases/tags/'))
      return existing ? json(200, existing) : json(404, {});
    if (url.includes('/releases?')) return json(200, releases);
    if (url.includes('/compare/'))
      return json(200, {
        html_url: 'https://github.com/o/r/compare/old...new',
        commits: [commit('2222222aaa', 'feat: a thing')],
      });
    return json(201, {
      id: 1,
      html_url: 'https://github.com/o/r/releases/tag/v2',
      upload_url: 'https://uploads/x{?name}',
      assets: [],
    });
  };
  const ctx = { http: { fetch }, log: () => {} } as unknown as Pick<Ctx, 'http' | 'log'>;
  return { ctx, calls };
}

const release = {
  apiUrl: 'https://api.github.com/',
  repo: 'o/r',
  token: 't',
  tag: 'v2',
  sha: 'new',
  name: 'R 2',
  body: 'Assets below.',
  prerelease: false,
  assets: [],
};

describe('githubRelease', () => {
  test("a new release lists the commits since the previous release's commit", async () => {
    const g = github([
      { tag_name: 'v0', target_commitish: 'draft', draft: true },
      { tag_name: 'v1', target_commitish: 'old', draft: false },
    ]);
    await githubRelease(g.ctx, release);
    expect(g.calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      'GET https://api.github.com/repos/o/r/releases/tags/v2',
      'GET https://api.github.com/repos/o/r/releases?per_page=10',
      'GET https://api.github.com/repos/o/r/compare/old...new',
      'POST https://api.github.com/repos/o/r/releases',
    ]);
    expect((g.calls[3]!.body as { body: string }).body).toBe(
      [
        'Assets below.',
        '## Changes',
        '### Features\n\n- a thing ([2222222](https://github.com/o/r/commit/2222222aaa))',
        '[All changes since v1](https://github.com/o/r/compare/old...new)',
      ].join('\n\n'),
    );
  });

  test('the first release has no notes', async () => {
    const g = github([]);
    await githubRelease(g.ctx, release);
    expect(g.calls.at(-1)!.body).toMatchObject({ body: 'Assets below.' });
  });

  test('an existing release is left as it is', async () => {
    const g = github([], {
      id: 1,
      tag_name: 'v2',
      html_url: 'h',
      upload_url: 'u',
      assets: [],
    });
    await githubRelease(g.ctx, release);
    expect(g.calls.map((c) => c.method)).toEqual(['GET']);
  });
});
