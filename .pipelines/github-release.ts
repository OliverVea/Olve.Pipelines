import { basename } from 'node:path';
import type { Ctx } from '@olivervea/pipelines';

interface Release {
  id: number;
  html_url: string;
  upload_url: string;
  assets: { name: string }[];
}

/**
 * Creates (or completes) the GitHub release `tag` on `sha` with `assets` attached. Safe to repeat: an
 * existing release is reused and assets it already has are left alone.
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
    const res = await ctx.http.fetch(api, {
      method: 'POST',
      headers: { ...headers, 'content-type': 'application/json' },
      body: JSON.stringify({
        tag_name: o.tag,
        target_commitish: o.sha,
        name: o.name,
        body: o.body,
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
