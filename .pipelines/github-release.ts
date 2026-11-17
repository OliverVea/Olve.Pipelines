import { basename } from 'node:path';
import type { Ctx } from '@olivervea/pipelines';

interface Release {
  id: number;
  tag_name: string;
  target_commitish: string;
  draft: boolean;
  html_url: string;
  upload_url: string;
  assets: { name: string }[];
}

/**
 * Creates (or completes) the GitHub release `tag` on `sha` with `assets` attached, its body followed by
 * the commits since the previous release. Safe to repeat: an existing release is reused as it is and
 * assets it already has are left alone.
 */
export async function githubRelease(
  ctx: Pick<Ctx, 'http' | 'log'>,
  o: {
    apiUrl: string;
    repo: string;
    token: string;
    tag: string;
    sha: string;
    name: string;
    body: string;
    prerelease: boolean;
    assets: string[];
  },
): Promise<string> {
  const api = `${o.apiUrl.replace(/\/+$/, '')}/repos/${o.repo}/releases`;
  const headers = {
    authorization: `Bearer ${o.token}`,
    accept: 'application/vnd.github+json',
    'x-github-api-version': '2022-11-28',
  };
  const byTag = async () => {
    const res = await ctx.http.fetch(`${api}/tags/${encodeURIComponent(o.tag)}`, {
      headers,
    });
    if (res.status === 404) return undefined;
    if (!res.ok)
      throw new Error(
        `GitHub release lookup of ${o.tag} failed: HTTP ${res.status} ${(await res.text()).slice(0, 200)}`,
      );
    return (await res.json()) as Release;
  };

  let release = await byTag();
  if (!release) {
    const notes = await changesSince(ctx, api, headers, o.tag, o.sha);
    const res = await ctx.http.fetch(api, {
      method: 'POST',
      headers: { ...headers, 'content-type': 'application/json' },
      body: JSON.stringify({
        tag_name: o.tag,
        target_commitish: o.sha,
        name: o.name,
        body: notes ? `${o.body}\n\n${notes}` : o.body,
        prerelease: o.prerelease,
        make_latest: o.prerelease ? 'false' : 'true',
      }),
    });
    // 422: another attempt created it after our lookup.
    if (res.status === 422) release = await byTag();
    else if (!res.ok)
      throw new Error(
        `creating GitHub release ${o.tag} failed: HTTP ${res.status} ${(await res.text()).slice(0, 300)}`,
      );
    else release = (await res.json()) as Release;
    if (!release)
      throw new Error(`GitHub release ${o.tag} could not be created or found`);
  }

  const have = new Set(release.assets.map((a) => a.name));
  const upload = release.upload_url.replace(/\{.*\}$/, '');
  for (const path of o.assets) {
    const name = basename(path);
    if (have.has(name)) {
      ctx.log('release asset already attached', { release: o.tag, asset: name });
      continue;
    }
    const file = Bun.file(path);
    const res = await ctx.http.fetch(`${upload}?name=${encodeURIComponent(name)}`, {
      method: 'POST',
      headers: {
        ...headers,
        'content-type': 'application/octet-stream',
        'content-length': String(file.size),
      },
      body: file,
    });
    if (!res.ok)
      throw new Error(
        `uploading ${name} to GitHub release ${o.tag} failed: HTTP ${res.status} ${(await res.text()).slice(0, 300)}`,
      );
    await res.body?.cancel().catch(() => {});
    ctx.log('release asset attached', { release: o.tag, asset: name, bytes: file.size });
  }
  ctx.log('GitHub release', { release: o.tag, url: release.html_url });
  return release.html_url;
}

interface Commit {
  sha: string;
  html_url: string;
  commit: { message: string };
}

async function changesSince(
  ctx: Pick<Ctx, 'http' | 'log'>,
  api: string,
  headers: Record<string, string>,
  tag: string,
  sha: string,
): Promise<string | undefined> {
  const get = async <T>(url: string): Promise<T> => {
    const res = await ctx.http.fetch(url, { headers });
    if (!res.ok)
      throw new Error(
        `GitHub ${new URL(url).pathname} failed: HTTP ${res.status} ${(await res.text()).slice(0, 200)}`,
      );
    return (await res.json()) as T;
  };
  const releases = await get<Release[]>(`${api}?per_page=10`);
  const previous = releases.find((r) => !r.draft && r.tag_name !== tag);
  if (!previous) {
    ctx.log('no previous GitHub release: no release notes');
    return undefined;
  }
  const compare = await get<{ html_url: string; commits: Commit[] }>(
    `${api.replace(/\/releases$/, '')}/compare/${previous.target_commitish}...${sha}`,
  );
  return releaseNotes(compare.commits, previous.tag_name, compare.html_url);
}

const groups: [type: string, title: string][] = [
  ['breaking', 'Breaking changes'],
  ['feat', 'Features'],
  ['fix', 'Fixes'],
  ['perf', 'Performance'],
  ['refactor', 'Refactoring'],
  ['docs', 'Docs'],
  ['other', 'Other'],
];
const hidden = new Set(['chore', 'test', 'ci', 'build', 'style']);

/** The commits grouped by conventional-commit type; chore, test, ci, build and style are left out. */
export function releaseNotes(
  commits: readonly Commit[],
  previousTag: string,
  compareUrl: string,
): string {
  const byGroup = new Map<string, string[]>();
  for (const c of commits) {
    const subject = c.commit.message.split('\n', 1)[0]!.trim();
    const m = /^(\w+)(?:\(([^)]*)\))?(!)?: (.+)$/.exec(subject);
    const type = m?.[1]!.toLowerCase();
    if (type && hidden.has(type) && !m![3]) continue;
    const group = !m
      ? 'other'
      : m[3]
        ? 'breaking'
        : groups.some(([g]) => g === type)
          ? type!
          : 'other';
    const text = m ? (m[2] ? `${m[2]}: ${m[4]}` : m[4]!) : subject;
    const lines = byGroup.get(group) ?? [];
    lines.push(`- ${text} ([${c.sha.slice(0, 7)}](${c.html_url}))`);
    byGroup.set(group, lines);
  }
  const sections = groups
    .filter(([g]) => byGroup.has(g))
    .map(([g, title]) => `### ${title}\n\n${byGroup.get(g)!.join('\n')}`);
  return [
    '## Changes',
    ...(sections.length ? sections : ['Nothing user-facing.']),
    `[All changes since ${previousTag}](${compareUrl})`,
  ].join('\n\n');
}
