// Library steps used by Olve.Pipelines' own deploy: gitHubSource, imageBuild, helmDeployOverSsh, httpHealthCheck.
// Run through testHandler with faked ctx parts; the assertions pin the exact commands they run.
import { describe, expect, test } from 'bun:test';
import { mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  checkSpa,
  httpHealthCheck,
  implement,
  model,
  pipeline,
  secret,
  serviceUrl,
  steps,
  z,
  type AnyStep,
  type Ctx,
  type FileRef,
} from '../../src/index.ts';
import { recordConfigRefs } from '../../src/core/config-refs.ts';
import { SSH_CLIENT_INSTALL } from '../../src/library/ssh.ts';
import {
  contentTagOf,
  dockerConfigJson,
  parseChallenge,
  reachableMirrors,
  registryUp,
  writeDockerConfig,
} from '../../src/library/image-build.ts';
import { sha256File } from '../../src/util/hash.ts';
import { dirEntries, writeTar } from '../../src/util/tar.ts';
import { describePipeline } from '../../src/describe/describe.ts';
import { Manifest } from '../../src/describe/manifest-schema.ts';
import { testFileRef, testHandler } from '../../src/testing/index.ts';
import { writeTarGz } from '../../src/util/tar.ts';
import { SHA, tmp } from './helpers.ts';

const settingsOf = (s: AnyStep) => recordConfigRefs(s.settings);

const TOKEN = secret('GITHUB_TOKEN', 'token');
const KEY = secret('SSH_PRIVATE_KEY', 'key');

/** ctx.retry without the waiting. */
const fastRetry: Ctx['retry'] = async (opts, fn) => {
  for (let i = 1; ; i++) {
    try {
      return await fn(i);
    } catch (e) {
      if (i >= opts.attempts) throw e;
    }
  }
};

const sourceRef = testFileRef({ step: 'source', path: 'tree/' });
const source = { repo: 'OliverVea/Olve.Pipelines', sha: SHA, tree: sourceRef };

describe('steps.gitHubSource', () => {
  const tarball = async () => {
    const dir = tmp('gh-tarball');
    const top = `OliverVea-Olve.Pipelines-${SHA.slice(0, 7)}`;
    mkdirSync(join(dir, top, 'helm'), { recursive: true });
    writeFileSync(join(dir, top, 'Dockerfile'), 'FROM scratch\n');
    writeFileSync(join(dir, top, 'helm', 'Chart.yaml'), 'name: x\n');
    const out = join(dir, 'repo.tar.gz');
    await writeTarGz(out, [
      { path: top, type: 'dir', mode: 0o755 },
      {
        path: `${top}/Dockerfile`,
        type: 'file',
        mode: 0o644,
        source: join(dir, top, 'Dockerfile'),
      },
      { path: `${top}/helm`, type: 'dir', mode: 0o755 },
      {
        path: `${top}/helm/Chart.yaml`,
        type: 'file',
        mode: 0o644,
        source: join(dir, top, 'helm', 'Chart.yaml'),
      },
    ]);
    return readFileSync(out);
  };

  test('downloads the tarball at the sha with the token and outputs the stripped tree', async () => {
    const step = steps.gitHubSource({ name: 'source', description: '', token: TOKEN });
    expect(step.secrets.map((s) => s.name)).toEqual(['GITHUB_TOKEN']);
    expect(step.image).toBe('alpine:3.22');
    const body = await tarball();
    const calls: { url: string; headers: Record<string, string> }[] = [];
    const fetch = async (url: string, init?: RequestInit) => {
      calls.push({ url, headers: init?.headers as Record<string, string> });
      return new Response(body);
    };
    const r = await testHandler(
      step,
      { repo: 'OliverVea/Olve.Pipelines', sha: SHA },
      {
        secrets: { GITHUB_TOKEN: 'ghp_test' },
        ctx: { http: { fetch } as unknown as Ctx['http'] },
      },
    );
    expect(r.status).toBe('succeeded');
    expect(calls).toEqual([
      {
        url: `https://api.github.com/repos/OliverVea/Olve.Pipelines/tarball/${SHA}`,
        headers: {
          accept: 'application/vnd.github+json',
          'user-agent': 'olve-pipelines',
          authorization: 'Bearer ghp_test',
        },
      },
    ]);
    if (r.status !== 'succeeded') return;
    expect(r.output).toMatchObject({
      repo: 'OliverVea/Olve.Pipelines',
      sha: SHA,
      tree: { step: 'source', path: 'tree/' },
    });
    expect(readFileSync(join(r.filesDir, 'tree.tar')).length).toBeGreaterThan(0);
  });

  test('the tree ref extracts to the repository root', async () => {
    const step = steps.gitHubSource({ name: 'source', description: '' });
    const body = await tarball();
    let extracted = '';
    const r = await testHandler(
      step,
      { repo: 'a/b', sha: SHA },
      {
        ctx: {
          http: { fetch: async () => new Response(body) } as unknown as Ctx['http'],
        },
      },
    );
    if (r.status !== 'succeeded') throw new Error(JSON.stringify(r));
    // Re-run a fetch of the produced ref through a real ctx bound to the same attempt.
    const check = await testHandler(
      implement(step.step, async (_i, ctx) => {
        extracted = await ctx.files.fetch(r.output.tree);
        return r.output;
      }),
      { repo: 'a/b', sha: SHA },
      { dir: r.dir },
    );
    expect(check.status).toBe('succeeded');
    expect(readFileSync(join(extracted, 'Dockerfile'), 'utf8')).toBe('FROM scratch\n');
    expect(readFileSync(join(extracted, 'helm', 'Chart.yaml'), 'utf8')).toBe('name: x\n');
  });

  test('no token: no authorization header and no secrets; 4xx fails at once, 5xx is retried', async () => {
    const step = steps.gitHubSource({ name: 'source', description: '' });
    expect(step.secrets).toEqual([]);
    let calls = 0;
    const notFound = await testHandler(
      step,
      { repo: 'a/b', sha: SHA },
      {
        ctx: {
          http: {
            fetch: async (_u: string, init?: RequestInit) => {
              calls++;
              expect(init?.headers).not.toHaveProperty('authorization');
              return new Response('nope', { status: 404 });
            },
          } as unknown as Ctx['http'],
          retry: fastRetry,
        },
      },
    );
    expect(notFound).toMatchObject({ status: 'failed', error: { kind: 'handler' } });
    if (notFound.status === 'failed')
      expect(notFound.error.message).toContain('HTTP 404');
    expect(calls).toBe(1);

    const body = await tarball();
    const statuses = [502, 503, 200];
    const flaky = await testHandler(
      step,
      { repo: 'a/b', sha: SHA },
      {
        ctx: {
          http: {
            fetch: async () => {
              const s = statuses.shift()!;
              return s === 200 ? new Response(body) : new Response('', { status: s });
            },
          } as unknown as Ctx['http'],
          retry: fastRetry,
        },
      },
    );
    expect(flaky.status).toBe('succeeded');
  });

  test('a declared token that is not set: fetched anonymously', async () => {
    const body = await tarball();
    let authorization: string | null = 'unset';
    const r = await testHandler(
      steps.gitHubSource({ name: 'source', description: '', token: TOKEN }),
      { repo: 'a/b', sha: SHA },
      {
        ctx: {
          http: {
            fetch: async (_u: string, init?: RequestInit) => {
              authorization = new Headers(init?.headers).get('authorization');
              return new Response(body);
            },
          } as unknown as Ctx['http'],
          retry: fastRetry,
        },
      },
    );
    expect(r.status).toBe('succeeded');
    expect(authorization).toBeNull();
  });

  test('rejects a malformed repo', async () => {
    const r = await testHandler(steps.gitHubSource({ name: 'source', description: '' }), {
      repo: 'not a repo',
      sha: SHA,
    });
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'input' } });
  });
});

describe('steps.imageBuild', () => {
  const fakeKaniko = (
    opts: { digest?: string; down?: string[]; hang?: string[] } = {},
  ) => {
    const tree = tmp('kaniko-tree');
    writeFileSync(join(tree, 'Dockerfile'), 'FROM scratch\n');
    const calls: string[][] = [];
    const execOpts: (Parameters<Ctx['exec']>[1] | undefined)[] = [];
    const exec: Ctx['exec'] = async (argv, o) => {
      calls.push([...argv]);
      execOpts.push(o);
      if (argv[0] === '/kaniko/executor') {
        const flag = (n: string) =>
          argv.find((a) => a.startsWith(`--${n}=`))!.slice(n.length + 3);
        writeFileSync(flag('tar-path'), 'image tar');
        if (opts.digest !== '')
          writeFileSync(
            flag('digest-file'),
            (opts.digest ?? 'sha256:' + 'ab'.repeat(32)) + '\n',
          );
      }
    };
    const fetched: FileRef[] = [];
    const files = { fetch: async (ref: FileRef) => (fetched.push(ref), tree) };
    const probed: string[] = [];
    const fetch = async (url: string, init?: RequestInit) => {
      probed.push(url);
      const host = new URL(url).host;
      if (opts.hang?.includes(host)) {
        return new Promise<Response>((_, reject) =>
          init?.signal?.addEventListener('abort', () => reject(init.signal!.reason)),
        );
      }
      if (opts.down?.includes(host)) throw new TypeError('fetch failed');
      return new Response('{}', { status: host.startsWith('hub') ? 200 : 401 });
    };
    const http = { fetch } as unknown as Ctx['http'];
    return { tree, calls, execOpts, exec, files, fetched, probed, http };
  };

  test('kaniko build: exact argv, BuiltImage output', async () => {
    const step = steps.imageBuild({
      name: 'build',
      description: '',
      repository: 'olve-pipelines-v2',
      timeoutSeconds: 3600,
    });
    expect(step).toMatchObject({
      image: 'gcr.io/kaniko-project/executor:debug',
      timeoutSeconds: 3600,
      retries: 2,
      secrets: [],
    });
    const k = fakeKaniko();
    const r = await testHandler(
      step,
      { source },
      {
        ctx: { exec: k.exec, files: k.files, http: k.http },
        run: { version: '2026.10.21.412' },
      },
    );
    expect(r.status).toBe('succeeded');
    expect(k.fetched).toEqual([sourceRef]);
    const out = join(r.dir, 'tmp');
    const kaniko = k.calls[2]!;
    const tarPath = kaniko
      .find((a) => a.startsWith('--tar-path='))!
      .slice('--tar-path='.length);
    expect(tarPath.startsWith(out)).toBe(true);
    expect(k.calls).toEqual([
      ['mkdir', '-p', '/kaniko/build-context'],
      ['cp', '-r', `${k.tree}/.`, '/kaniko/build-context'],
      [
        '/kaniko/executor',
        '--context=/kaniko/build-context',
        '--dockerfile=/kaniko/build-context/Dockerfile',
        '--no-push',
        `--tar-path=${tarPath}`,
        `--digest-file=${tarPath.replace(/image\.tar$/, 'digest')}`,
        '--destination=olve-pipelines-v2:2026.10.21.412',
        '--snapshot-mode=redo',
        '--use-new-run',
        '--compressed-caching=false',
      ],
    ]);
    expect(k.probed).toEqual([]);
    expect(k.execOpts[2]).toEqual({ idleTimeoutSeconds: 300 }); // stall watchdog on by default
    if (r.status !== 'succeeded') return;
    expect(r.output).toMatchObject({
      repository: 'docker.io/library/olve-pipelines-v2',
      tag: '2026.10.21.412',
      digest: 'sha256:' + 'ab'.repeat(32),
      tar: { step: 'build', path: 'image.tar' },
    });
    expect(readFileSync(join(r.filesDir, 'image.tar'), 'utf8')).toBe('image tar');
  });

  test('no mapping with registryMap: {}; reruns get a distinct tag', async () => {
    const k = fakeKaniko();
    const step = steps.imageBuild({
      name: 'build',
      description: '',
      repository: 'registry.ovea.pro/app',
      registryMap: {},
    });
    expect(settingsOf(step)).toEqual({ repository: 'registry.ovea.pro/app' });
    const r = await testHandler(
      step,
      { source },
      {
        ctx: { exec: k.exec, files: k.files, http: k.http },
        run: { version: '2026.10.21.7', attempt: 2 },
      },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { repository: 'registry.ovea.pro/app', tag: '2026.10.21.7-attempt2' },
    });
    expect(k.calls[2]).toContain(
      '--destination=registry.ovea.pro/app:2026.10.21.7-attempt2',
    );
    expect(k.calls[2]!.some((a) => a.startsWith('--cache'))).toBe(false);
    expect(k.calls[2]!.some((a) => a.startsWith('--registry-map'))).toBe(false);
    expect(k.probed).toEqual([]);
  });

  const mirrors = {
    'mcr.microsoft.com': 'mcr-cache.example.com',
    'index.docker.io': 'hub-cache.example.com',
  };

  test('registryMap: none by default; a map is recorded in the manifest config and probed before kaniko', async () => {
    expect(
      settingsOf(steps.imageBuild({ name: 'build', description: '', repository: 'x' })),
    ).toEqual({ repository: 'x' });
    const step = steps.imageBuild({
      name: 'build',
      description: '',
      repository: 'x',
      registryMap: mirrors,
    });
    expect(settingsOf(step)).toEqual({ repository: 'x', registryMap: mirrors });
    const k = fakeKaniko();
    expect(
      (
        await testHandler(
          step,
          { source },
          { ctx: { exec: k.exec, files: k.files, http: k.http } },
        )
      ).status,
    ).toBe('succeeded');
    expect(k.calls[2]!.filter((a) => a.startsWith('--registry-map='))).toEqual([
      '--registry-map=mcr.microsoft.com=mcr-cache.example.com',
      '--registry-map=index.docker.io=hub-cache.example.com',
    ]);
    expect(k.probed.toSorted()).toEqual([
      'https://hub-cache.example.com/v2/',
      'https://mcr-cache.example.com/v2/',
    ]);
  });

  test('an unreachable mirror never blocks the build: its mapping is dropped', async () => {
    const step = steps.imageBuild({
      name: 'build',
      description: '',
      repository: 'x',
      registryMap: mirrors,
    });
    const k = fakeKaniko({ down: ['mcr-cache.example.com'] });
    const r = await testHandler(
      step,
      { source },
      { ctx: { exec: k.exec, files: k.files, http: k.http } },
    );
    expect(r.status).toBe('succeeded');
    expect(k.calls[2]!.filter((a) => a.startsWith('--registry-map='))).toEqual([
      '--registry-map=index.docker.io=hub-cache.example.com',
    ]);
    expect(r.logs.join('\n')).toContain(
      'mcr-cache.example.com unreachable; pulling from mcr.microsoft.com directly',
    );
  });

  test('cache probe: 200/401 = up, other statuses, errors and timeouts = down; one probe per host', async () => {
    const statusOf = (status: number) =>
      ({ fetch: async () => new Response('', { status }) }) as unknown as Ctx['http'];
    expect(await registryUp(statusOf(200), 'h')).toBe(true);
    expect(await registryUp(statusOf(401), 'h')).toBe(true);
    expect(await registryUp(statusOf(404), 'h')).toBe(false);
    expect(await registryUp(statusOf(503), 'h')).toBe(false);
    const hang = fakeKaniko({ hang: ['slow.example'] });
    const t0 = Date.now();
    expect(await registryUp(hang.http, 'slow.example', undefined, 50)).toBe(false);
    expect(Date.now() - t0).toBeLessThan(2000);
    const k = fakeKaniko({ down: ['registry.example'] });
    const logs: string[] = [];
    const ctx = {
      http: k.http,
      log: (m: string) => void logs.push(m),
      signal: new AbortController().signal,
    } as unknown as Ctx;
    const got = await reachableMirrors(ctx, {
      'mcr.microsoft.com': 'registry.example/mirror/mcr',
      'index.docker.io': 'registry.example/mirror/hub',
      'ghcr.io': 'hub-cache.ovea.pro',
    });
    expect(got).toEqual({ 'ghcr.io': 'hub-cache.ovea.pro' });
    expect(k.probed.toSorted()).toEqual([
      'https://hub-cache.ovea.pro/v2/',
      'https://registry.example/v2/',
    ]);
    expect(logs).toEqual([
      'registry.example unreachable; pulling from mcr.microsoft.com directly',
      'registry.example unreachable; pulling from index.docker.io directly',
    ]);
  });

  test('registryMap: one --registry-map per entry, recorded in the manifest config', async () => {
    const registryMap = {
      'mcr.microsoft.com': 'registry.example/mirror/mcr.microsoft.com',
      'index.docker.io': 'registry.example/mirror/docker.io',
    };
    const step = steps.imageBuild({
      name: 'build',
      description: '',
      repository: 'x',
      registryMap,
    });
    const k = fakeKaniko();
    const r = await testHandler(
      step,
      { source },
      { ctx: { exec: k.exec, files: k.files, http: k.http } },
    );
    expect(r.status).toBe('succeeded');
    expect(k.calls[2]!.filter((a) => a.startsWith('--registry-map='))).toEqual([
      '--registry-map=mcr.microsoft.com=registry.example/mirror/mcr.microsoft.com',
      '--registry-map=index.docker.io=registry.example/mirror/docker.io',
    ]);
    expect(settingsOf(step)).toEqual({ repository: 'x', registryMap });
  });

  test("stallTimeoutSeconds and retries: passed to kaniko's exec and into the step contract", async () => {
    const step = steps.imageBuild({
      name: 'build',
      description: '',
      repository: 'x',
      stallTimeoutSeconds: 120,
      retries: 2,
    });
    expect(step).toMatchObject({ retries: 2 });
    expect(
      steps.imageBuild({ name: 'build', description: '', repository: 'x' }),
    ).toMatchObject({ retries: 2 }); // default
    expect(
      steps.imageBuild({ name: 'build', description: '', repository: 'x', retries: 0 }),
    ).toMatchObject({ retries: 0 });
    const k = fakeKaniko();
    const r = await testHandler(
      step,
      { source },
      { ctx: { exec: k.exec, files: k.files, http: k.http } },
    );
    expect(r.status).toBe('succeeded');
    expect(k.execOpts[2]).toEqual({ idleTimeoutSeconds: 120 });
  });

  test('a missing digest file or an invalid tag fails the step', async () => {
    const k = fakeKaniko({ digest: '' });
    const r = await testHandler(
      steps.imageBuild({ name: 'build', description: '', repository: 'x' }),
      { source },
      { ctx: { exec: k.exec, files: k.files, http: k.http } },
    );
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'handler' } });
    if (r.status === 'failed')
      expect(r.error.message).toContain('did not write the image digest');
    const bad = await testHandler(
      steps.imageBuild({ name: 'build', description: '', repository: 'x' }),
      { source },
      {
        ctx: { exec: fakeKaniko().exec, files: k.files, http: k.http },
        run: { version: '1+2' },
      },
    );
    if (bad.status === 'failed')
      expect(bad.error.message).toContain('not a valid image tag');
    else throw new Error('expected failure');
  });
});

describe('steps.imageBuild: context, push', () => {
  const kaniko = (opts: { tagDigest?: string } = {}) => {
    const tree = tmp('kaniko-ctx-tree');
    mkdirSync(join(tree, 'src/frontend'), { recursive: true });
    writeFileSync(join(tree, 'src/frontend/Dockerfile'), 'FROM nginx\n');
    writeFileSync(join(tree, 'src/frontend/index.html'), '<html></html>\n');
    writeFileSync(join(tree, 'Dockerfile'), 'FROM scratch\n');
    const calls: string[][] = [];
    const exec: Ctx['exec'] = async (argv) => {
      calls.push([...argv]);
      if (argv[0] === '/kaniko/executor') {
        const flag = (n: string) =>
          argv.find((a) => a.startsWith(`--${n}=`))?.slice(n.length + 3);
        const tarPath = flag('tar-path');
        if (tarPath) writeFileSync(tarPath, 'image tar');
        writeFileSync(flag('digest-file')!, 'sha256:' + 'cd'.repeat(32));
      }
    };
    const heads: string[] = [];
    const http = {
      fetch: async (url: string, init?: RequestInit) => {
        if (init?.method === 'HEAD') {
          heads.push(url);
          return opts.tagDigest
            ? new Response(null, {
                status: 200,
                headers: { 'Docker-Content-Digest': opts.tagDigest },
              })
            : new Response(null, { status: 404 });
        }
        return new Response('{}', { status: 200 });
      },
    } as unknown as Ctx['http'];
    return {
      tree,
      calls,
      exec,
      heads,
      ctx: { exec, http, files: { fetch: async () => tree } },
    };
  };

  test('context: kaniko builds the subdirectory, recorded in the manifest config', async () => {
    const step = steps.imageBuild({
      name: 'build-web',
      description: '',
      repository: 'web',
      context: 'src/frontend',
      registryMap: {},
    });
    expect(settingsOf(step)).toEqual({ repository: 'web', context: 'src/frontend' });
    const k = kaniko();
    const r = await testHandler(
      step,
      { source },
      { ctx: k.ctx, run: { version: '2026.10.22.3' } },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { repository: 'docker.io/library/web', tag: '2026.10.22.3' },
    });
    expect(k.calls[1]).toEqual(['cp', '-r', `${k.tree}/.`, '/kaniko/build-context']);
    expect(k.calls[2]!.slice(1, 3)).toEqual([
      '--context=/kaniko/build-context/src/frontend',
      '--dockerfile=/kaniko/build-context/src/frontend/Dockerfile',
    ]);
  });

  test('a missing context or Dockerfile fails the step; a path outside the tree is a definition error', async () => {
    const missing = await testHandler(
      steps.imageBuild({ name: 'b', description: '', repository: 'x', context: 'nope' }),
      { source },
      { ctx: kaniko().ctx },
    );
    if (missing.status !== 'failed') throw new Error('expected failure');
    expect(missing.error.message).toContain("build context 'nope' not found");
    const noFile = await testHandler(
      steps.imageBuild({ name: 'b', description: '', repository: 'x', context: 'src' }),
      { source },
      { ctx: kaniko().ctx },
    );
    if (noFile.status !== 'failed') throw new Error('expected failure');
    expect(noFile.error.message).toContain(
      "Dockerfile not found in the build context 'src'",
    );
    expect(() =>
      steps.imageBuild({ name: 'b', description: '', repository: 'x', context: '../up' }),
    ).toThrow('relative path inside the source tree');
  });

  test('push: kaniko pushes (no --no-push, no tarball) and the output is a PushedImage', async () => {
    const step = steps.imageBuild({
      name: 'build',
      description: '',
      repository: 'registry.ovea.pro/app/api',
      push: true,
      registryMap: {},
    });
    expect(step.output.name).toBe('PushedImage');
    expect(settingsOf(step)).toEqual({
      repository: 'registry.ovea.pro/app/api',
      push: true,
    });
    const k = kaniko();
    const r = await testHandler(
      step,
      { source },
      { ctx: k.ctx, run: { version: '2026.10.22.3' } },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: {
        repository: 'registry.ovea.pro/app/api',
        tag: '2026.10.22.3',
        digest: 'sha256:' + 'cd'.repeat(32),
      },
    });
    if (r.status === 'succeeded')
      expect(Object.keys(r.output!)).toEqual(['repository', 'tag', 'digest']);
    const argv = k.calls[2]!;
    expect(argv).toContain('--destination=registry.ovea.pro/app/api:2026.10.22.3');
    expect(argv.some((a) => a === '--no-push' || a.startsWith('--tar-path'))).toBe(false);
    expect(argv.some((a) => a.startsWith('--digest-file='))).toBe(true);
    expect(k.heads).toEqual([]);
    expect(() =>
      steps.imageBuild({ name: 'b', description: '', repository: 'app', push: true }),
    ).toThrow('registry host');
  });

  test("contentTag: tag from the context's deterministic tar; an existing tag skips kaniko", async () => {
    const def = {
      name: 'build-figures',
      description: '',
      repository: 'registry.ovea.pro/qb/figures',
      context: 'src/frontend',
      push: true,
      contentTag: true,
      registryMap: {},
    } as const;
    const k = kaniko();
    const ref = join(tmp('content-tag-ref'), 'ref.tar');
    writeTar(ref, dirEntries(join(k.tree, 'src/frontend')));
    const tag = `src-${sha256File(ref).slice(0, 12)}`;
    expect(contentTagOf(join(k.tree, 'src/frontend'), tmp('content-tag'))).toBe(tag);

    const built = await testHandler(steps.imageBuild(def), { source }, { ctx: k.ctx });
    expect(built).toMatchObject({ status: 'succeeded', output: { tag } });
    expect(k.heads).toEqual([`https://registry.ovea.pro/v2/qb/figures/manifests/${tag}`]);
    expect(k.calls[2]).toContain(`--destination=registry.ovea.pro/qb/figures:${tag}`);

    const existing = 'sha256:' + 'ef'.repeat(32);
    const k2 = kaniko({ tagDigest: existing });
    const skipped = await testHandler(steps.imageBuild(def), { source }, { ctx: k2.ctx });
    expect(skipped).toMatchObject({
      status: 'succeeded',
      output: { repository: 'registry.ovea.pro/qb/figures', tag, digest: existing },
    });
    expect(k2.calls).toEqual([]);
  });
});

describe('steps.imageBuild: registry login and plain-HTTP registries', () => {
  const GHCR = secret('GHCR_TOKEN', 'ghcr token');
  const digest = 'sha256:' + 'cd'.repeat(32);
  const setup = (registry: (url: string, init: RequestInit | undefined) => Response) => {
    const tree = tmp('kaniko-auth-tree');
    mkdirSync(join(tree, 'harness/base'), { recursive: true });
    writeFileSync(join(tree, 'harness/base/Dockerfile'), 'FROM scratch\n');
    writeFileSync(join(tree, 'Dockerfile'), 'FROM scratch\n');
    const calls: string[][] = [];
    const requests: { url: string; method: string; authorization: string | null }[] = [];
    const exec: Ctx['exec'] = async (argv) => {
      calls.push([...argv]);
      if (argv[0] === '/kaniko/executor')
        writeFileSync(
          argv.find((a) => a.startsWith('--digest-file='))!.slice(14),
          digest,
        );
    };
    const http = {
      fetch: async (url: string, init?: RequestInit) => {
        requests.push({
          url,
          method: init?.method ?? 'GET',
          authorization: new Headers(init?.headers).get('authorization'),
        });
        return registry(url, init);
      },
    } as unknown as Ctx['http'];
    const dockerConfig = tmp('docker-config');
    return {
      calls,
      requests,
      dockerConfig,
      ctx: { exec, http, files: { fetch: async () => tree } },
    };
  };
  const withDockerConfig = async <T>(dir: string, fn: () => Promise<T>): Promise<T> => {
    const before = process.env.DOCKER_CONFIG;
    process.env.DOCKER_CONFIG = dir;
    try {
      return await fn();
    } finally {
      if (before === undefined) delete process.env.DOCKER_CONFIG;
      else process.env.DOCKER_CONFIG = before;
    }
  };

  test('registryAuth: the secret is declared, kaniko gets a docker config, the lookup answers a Bearer challenge', async () => {
    const step = steps.imageBuild({
      name: 'publish-harness',
      description: '',
      repository: 'ghcr.io/olivervea/harness-base',
      context: 'harness/base',
      push: true,
      contentTag: true,
      registryMap: {},
      registryAuth: { username: 'olivervea', password: GHCR },
    });
    expect(step.secrets).toEqual([GHCR]);
    expect(settingsOf(step)).toEqual({
      repository: 'ghcr.io/olivervea/harness-base',
      context: 'harness/base',
      push: true,
      contentTag: true,
      registryAuth: { username: 'olivervea', password: 'GHCR_TOKEN' },
    });
    const s = setup((url, init) => {
      if (url.startsWith('https://ghcr.io/token'))
        return Response.json({ token: 'registry-token' });
      const auth = new Headers(init?.headers).get('authorization');
      if (!auth)
        return new Response(null, {
          status: 401,
          headers: {
            'WWW-Authenticate':
              'Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:olivervea/harness-base:pull"',
          },
        });
      return new Response(null, { status: 404 });
    });
    const r = await withDockerConfig(s.dockerConfig, () =>
      testHandler(
        step,
        { source },
        { ctx: s.ctx, secrets: { GHCR_TOKEN: 'ghp_secret' } },
      ),
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { repository: 'ghcr.io/olivervea/harness-base', digest },
    });
    const basic = `Basic ${Buffer.from('olivervea:ghp_secret').toString('base64')}`;
    expect(
      s.requests.map((q) => [q.method, q.url.split('/manifests/')[0], q.authorization]),
    ).toEqual([
      ['HEAD', 'https://ghcr.io/v2/olivervea/harness-base', null],
      [
        'GET',
        'https://ghcr.io/token?service=ghcr.io&scope=repository%3Aolivervea%2Fharness-base%3Apull',
        basic,
      ],
      ['HEAD', 'https://ghcr.io/v2/olivervea/harness-base', 'Bearer registry-token'],
    ]);
    expect(JSON.parse(readFileSync(join(s.dockerConfig, 'config.json'), 'utf8'))).toEqual(
      {
        auths: {
          'ghcr.io': { auth: Buffer.from('olivervea:ghp_secret').toString('base64') },
        },
      },
    );
    expect(statSync(join(s.dockerConfig, 'config.json')).mode & 0o777).toBe(0o600);
    expect(
      s.calls
        .find((c) => c[0] === '/kaniko/executor')!
        .some((a) => a.startsWith('--insecure')),
    ).toBe(false);
  });

  test('insecureRegistry with Basic auth: plain HTTP lookups, kaniko --insecure; an existing tag skips the build', async () => {
    const REG = secret('REGISTRY_PASSWORD', 'password');
    const step = steps.imageBuild({
      name: 'publish',
      description: '',
      repository: 'registry:5000/harness',
      context: 'harness/base',
      push: true,
      contentTag: true,
      registryMap: {},
      insecureRegistry: true,
      registryAuth: { username: 'local', password: REG },
    });
    const existing = 'sha256:' + 'ef'.repeat(32);
    let present = false;
    const s = setup((_url, init) => {
      if (
        new Headers(init?.headers).get('authorization') !==
        `Basic ${Buffer.from('local:pw').toString('base64')}`
      )
        return new Response(null, {
          status: 401,
          headers: { 'WWW-Authenticate': 'Basic realm="Registry Realm"' },
        });
      return present
        ? new Response(null, {
            status: 200,
            headers: { 'Docker-Content-Digest': existing },
          })
        : new Response(null, { status: 404 });
    });
    const built = await withDockerConfig(s.dockerConfig, () =>
      testHandler(step, { source }, { ctx: s.ctx, secrets: { REGISTRY_PASSWORD: 'pw' } }),
    );
    expect(built).toMatchObject({ status: 'succeeded', output: { digest } });
    expect(
      s.requests.every((q) =>
        q.url.startsWith('http://registry:5000/v2/harness/manifests/src-'),
      ),
    ).toBe(true);
    const argv = s.calls.find((c) => c[0] === '/kaniko/executor')!;
    expect(argv).toContain('--insecure');
    expect(argv).toContain('--insecure-registry=registry:5000');

    present = true;
    s.calls.length = 0;
    const skipped = await withDockerConfig(s.dockerConfig, () =>
      testHandler(step, { source }, { ctx: s.ctx, secrets: { REGISTRY_PASSWORD: 'pw' } }),
    );
    expect(skipped).toMatchObject({ status: 'succeeded', output: { digest: existing } });
    expect(s.calls).toEqual([]);

    const wrong = await withDockerConfig(s.dockerConfig, () =>
      testHandler(
        step,
        { source },
        { ctx: s.ctx, secrets: { REGISTRY_PASSWORD: 'nope' } },
      ),
    );
    if (wrong.status !== 'failed') throw new Error('expected failure');
    expect(wrong.error.message).toContain('answered HTTP 401');
  });

  test('an unset registryAuth secret fails the step; an anonymous denied lookup builds', async () => {
    const step = steps.imageBuild({
      name: 'p',
      description: '',
      repository: 'ghcr.io/o/x',
      push: true,
      contentTag: true,
      registryMap: {},
      registryAuth: { username: 'o', password: GHCR },
    });
    const s = setup(() => new Response(null, { status: 404 }));
    const unset = await withDockerConfig(s.dockerConfig, () =>
      testHandler(step, { source }, { ctx: s.ctx, secrets: {} }),
    );
    if (unset.status !== 'failed') throw new Error('expected failure');
    expect(unset.error.message).toContain('secret GHCR_TOKEN is not set');

    const anon = steps.imageBuild({
      name: 'p',
      description: '',
      repository: 'ghcr.io/o/x',
      push: true,
      contentTag: true,
      registryMap: {},
    });
    const denied = setup((url) =>
      url.includes('/token')
        ? new Response(null, { status: 403 })
        : new Response(null, {
            status: 401,
            headers: { 'WWW-Authenticate': 'Bearer realm="https://ghcr.io/token"' },
          }),
    );
    expect(await testHandler(anon, { source }, { ctx: denied.ctx })).toMatchObject({
      status: 'succeeded',
    });
    expect(denied.calls.some((c) => c[0] === '/kaniko/executor')).toBe(true);
  });

  test('a plain-HTTP mirror is probed over HTTP', async () => {
    const probed: string[] = [];
    const http = {
      fetch: async (url: string) => (
        probed.push(url),
        new Response(null, { status: 401 })
      ),
    } as unknown as Ctx['http'];
    const got = await reachableMirrors(
      { http, log: () => {}, signal: new AbortController().signal },
      { 'index.docker.io': 'registry:5000/hub' },
      ['registry:5000'],
    );
    expect(probed).toEqual(['http://registry:5000/v2/']);
    expect(got).toEqual({ 'index.docker.io': 'registry:5000/hub' });
  });

  test('definition errors and challenge parsing', () => {
    expect(() =>
      steps.imageBuild({
        name: 'b',
        description: '',
        repository: 'app',
        insecureRegistry: true,
      }),
    ).toThrow('registry host');
    expect(
      parseChallenge(
        'Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:a/b:pull"',
      ),
    ).toEqual({
      scheme: 'bearer',
      params: {
        realm: 'https://ghcr.io/token',
        service: 'ghcr.io',
        scope: 'repository:a/b:pull',
      },
    });
    expect(parseChallenge('Basic realm="Registry Realm"')).toEqual({
      scheme: 'basic',
      params: { realm: 'Registry Realm' },
    });
    expect(JSON.parse(dockerConfigJson('ghcr.io', 'u', 'p'))).toEqual({
      auths: { 'ghcr.io': { auth: 'dTpw' } },
    });
    const dir = tmp('docker-config-merge');
    writeFileSync(
      join(dir, 'config.json'),
      JSON.stringify({
        credHelpers: { 'gcr.io': 'gcr' },
        auths: { 'other.io': { auth: 'eA==' } },
      }),
    );
    writeDockerConfig(dir, 'ghcr.io', 'u', 'p');
    expect(JSON.parse(readFileSync(join(dir, 'config.json'), 'utf8'))).toEqual({
      credHelpers: { 'gcr.io': 'gcr' },
      auths: { 'other.io': { auth: 'eA==' }, 'ghcr.io': { auth: 'dTpw' } },
    });
  });
});

describe('steps.helmDeployOverSsh', () => {
  const def = {
    name: 'deploy-beta',
    description: '',
    host: 'oliver@bulwark-m2',
    sshKey: KEY,
    release: 'olve-pipelines-v2',
    namespace: 'apps-beta',
    chart: 'helm',
    values: ['values-beta.yaml', 'values-v2-beta.yaml'],
    set: { 'slo.enabled': 'false' },
    healthUrl: 'http://olve-pipelines-v2.apps-beta.svc.cluster.local/api/health',
  } as const;
  const image = {
    repository: 'docker.io/library/olve-pipelines-v2',
    tag: '2026.10.21.412',
    digest: 'sha256:' + 'ab'.repeat(32),
    tar: testFileRef({ step: 'build', path: 'image.tar' }),
  };

  const fakes = (opts: { chart?: boolean; healthy?: number } = {}) => {
    const tree = tmp('helm-tree');
    if (opts.chart !== false) mkdirSync(join(tree, 'helm'));
    const tar = join(tmp('helm-image'), 'image.tar');
    writeFileSync(tar, 'tar');
    const calls: (string[] | string)[] = [];
    let health = 0;
    const ctx: Partial<Ctx<'SSH_PRIVATE_KEY'>> = {
      files: { fetch: async (ref: FileRef) => (ref.path === 'tree/' ? tree : tar) },
      exec: async (argv) => void calls.push([...argv]),
      sh: Object.assign(
        async (cmd: string | { script: string }) => void calls.push(cmd as string),
        { text: async () => '' },
      ),
      http: {
        expectOk: async (url: string) => {
          calls.push(`GET ${url}`);
          if (++health < (opts.healthy ?? 1)) throw new Error('HTTP 503');
          return new Response('ok');
        },
      } as unknown as Ctx['http'],
      retry: fastRetry,
    };
    return { tree, tar, calls, ctx };
  };

  test('imports the image, copies the chart, helm-upgrades, waits for rollout, checks health', async () => {
    const step = steps.helmDeployOverSsh(def);
    expect(step).toMatchObject({ image: 'alpine:3.22', secrets: [KEY] });
    const f = fakes({ healthy: 3 });
    const r = await testHandler(
      step,
      { source, image },
      { secrets: { SSH_PRIVATE_KEY: '-----KEY-----' }, ctx: f.ctx },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: {
        release: 'olve-pipelines-v2',
        namespace: 'apps-beta',
        image: 'docker.io/library/olve-pipelines-v2:2026.10.21.412',
      },
    });

    const key = join(r.dir, 'tmp', 'ssh', 'id');
    expect(readFileSync(key, 'utf8')).toBe('-----KEY-----\n');
    expect(statSync(key).mode & 0o777).toBe(0o600);
    const o = `-i ${key} -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o BatchMode=yes`;
    const ssh = (remote: string) => ['ssh', ...o.split(' '), 'oliver@bulwark-m2', remote];
    const remote = '/tmp/olve-pipelines-v2-helm-apps-beta';
    expect(f.calls).toEqual([
      SSH_CLIENT_INSTALL,
      `ssh ${o} oliver@bulwark-m2 'sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load' < ${f.tar}`,
      ssh(`rm -rf ${remote}`),
      ['scp', ...o.split(' '), '-r', join(f.tree, 'helm'), `oliver@bulwark-m2:${remote}`],
      ssh(
        `cd ${remote} && helm upgrade --install olve-pipelines-v2 . -n apps-beta -f values-beta.yaml -f values-v2-beta.yaml ` +
          `--set slo.enabled=false --set image.repository=docker.io/library/olve-pipelines-v2 --set image.tag=2026.10.21.412 ` +
          `--set image.pullPolicy=Never && cd / && rm -rf ${remote}`,
      ),
      ssh('kubectl -n apps-beta rollout status deploy/olve-pipelines-v2 --timeout=120s'),
      `GET ${def.healthUrl}`,
      `GET ${def.healthUrl}`,
      `GET ${def.healthUrl}`,
    ]);
  });

  test('remote arguments are shell-quoted', async () => {
    const f = fakes();
    const r = await testHandler(
      steps.helmDeployOverSsh({
        ...def,
        set: { 'a.b': 'x y', c: "it's" },
        healthUrl: undefined,
      }),
      { source, image },
      {
        secrets: { SSH_PRIVATE_KEY: 'k\n' },
        ctx: f.ctx,
      },
    );
    expect(r.status).toBe('succeeded');
    const helm = (f.calls[4] as string[]).at(-1)!;
    expect(helm).toContain(`--set 'a.b=x y' --set 'c=it'\\''s' --set image.repository=`);
    expect(f.calls.some((c) => typeof c === 'string' && c.startsWith('GET'))).toBe(false);
  });

  test('a missing chart or a failing health check fails the step', async () => {
    const noChart = await testHandler(
      steps.helmDeployOverSsh(def),
      { source, image },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: fakes({ chart: false }).ctx },
    );
    expect(noChart).toMatchObject({ status: 'failed', error: { kind: 'handler' } });
    if (noChart.status === 'failed')
      expect(noChart.error.message).toContain("chart directory 'helm' not found");

    const f = fakes({ healthy: 99 });
    const unhealthy = await testHandler(
      steps.helmDeployOverSsh(def),
      { source, image },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: f.ctx },
    );
    expect(unhealthy).toMatchObject({ status: 'failed', error: { kind: 'handler' } });
    expect(
      f.calls.filter((c) => typeof c === 'string' && c.startsWith('GET')),
    ).toHaveLength(5);
  });

  test('a failing remote command fails the step and stops the deploy', async () => {
    const f = fakes();
    const exec = f.ctx.exec!;
    f.ctx.exec = async (argv, o) => {
      await exec(argv, o);
      if (argv[0] === 'ssh' && argv.at(-1)!.includes('helm upgrade'))
        throw new Error('`ssh …` failed with exit code 1');
    };
    const r = await testHandler(
      steps.helmDeployOverSsh(def),
      { source, image },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: f.ctx },
    );
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'handler' } });
    expect(
      f.calls.some((c) => Array.isArray(c) && c.at(-1)!.includes('rollout status')),
    ).toBe(false);
  });
});

describe('steps.helmDeployOverSsh: several images, registry images, rollouts', () => {
  const ref = (path: string) => testFileRef({ step: 'x', path });
  const built = (repository: string, path: string) => ({
    repository,
    tag: '2026.10.22.3',
    digest: 'sha256:' + 'ab'.repeat(32),
    tar: ref(path),
  });
  const fakes = () => {
    const tree = tmp('helm-multi-tree');
    mkdirSync(join(tree, 'helm'));
    mkdirSync(join(tree, 'services/figures/helm'), { recursive: true });
    const tars = tmp('helm-multi-tars');
    const calls: (string[] | string)[] = [];
    const health: { url: string }[] = [];
    const ctx: Partial<Ctx<'SSH_PRIVATE_KEY'>> = {
      files: {
        fetch: async (r: FileRef) => (r.path === 'tree/' ? tree : join(tars, r.path)),
      },
      exec: async (argv) => void calls.push([...argv]),
      sh: Object.assign(
        async (cmd: string | { script: string }) => void calls.push(cmd as string),
        { text: async () => '' },
      ),
      http: {
        expectOk: async (url: string) => (health.push({ url }), new Response('ok')),
      } as unknown as Ctx['http'],
      retry: (async (opts, fn) => (
        calls.push(`retry ${opts.attempts}x${opts.delaySeconds}`),
        fastRetry(opts, fn)
      )) as Ctx['retry'],
    };
    return { tree, tars, calls, ctx };
  };
  const sshCmd = (calls: (string[] | string)[]) =>
    calls
      .filter((c): c is string[] => Array.isArray(c) && c[0] === 'ssh')
      .map((c) => c.at(-1)!);

  test('several images: each tar imported, one values prefix per image, every rollout waited on', async () => {
    const step = steps.helmDeployOverSsh({
      name: 'deploy-beta',
      description: '',
      host: 'u@h',
      sshKey: KEY,
      release: 'slimes',
      namespace: 'apps-beta',
      chart: 'helm',
      images: { server: 'image', web: 'web.image' },
      deployments: ['slimes-server', 'slimes-web'],
    });
    expect(Object.keys(step.input)).toEqual(['source', 'server', 'web']);
    expect(settingsOf(step)).toMatchObject({
      images: { server: 'image', web: 'web.image' },
      deployments: ['slimes-server', 'slimes-web'],
    });
    const f = fakes();
    const r = await testHandler(
      step,
      {
        source,
        server: built('docker.io/library/slimes', 'server.tar'),
        web: built('docker.io/library/slimes-web', 'web.tar'),
      },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: f.ctx },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: {
        release: 'slimes',
        namespace: 'apps-beta',
        image: 'docker.io/library/slimes:2026.10.22.3',
      },
    });
    const imports = f.calls.filter(
      (c): c is string => typeof c === 'string' && c.includes(" load' < "),
    );
    expect(imports.map((c) => c.split(' < ')[1])).toEqual([
      join(f.tars, 'server.tar'),
      join(f.tars, 'web.tar'),
    ]);
    const remote = sshCmd(f.calls);
    expect(remote[1]).toContain(
      '--set image.repository=docker.io/library/slimes --set image.tag=2026.10.22.3 --set image.pullPolicy=Never ' +
        '--set web.image.repository=docker.io/library/slimes-web --set web.image.tag=2026.10.22.3 --set web.image.pullPolicy=Never',
    );
    expect(remote.slice(2)).toEqual([
      'kubectl -n apps-beta rollout status deploy/slimes-server --timeout=120s',
      'kubectl -n apps-beta rollout status deploy/slimes-web --timeout=120s',
    ]);
  });

  test('fromRegistry: PushedImage inputs pulled on the host, pullPolicy=IfNotPresent; rollout timeout and health options', async () => {
    const step = steps.helmDeployOverSsh({
      name: 'deploy-figures',
      description: '',
      host: 'u@h',
      sshKey: KEY,
      release: 'figures',
      namespace: 'apps',
      chart: 'services/figures/helm',
      fromRegistry: true,
      rolloutTimeoutSeconds: 300,
      healthUrl: { url: 'http://figures.apps.svc.cluster.local/health', attempts: 6 },
    });
    expect(step.input.image.name).toBe('PushedImage');
    expect(settingsOf(step)).toEqual({
      host: 'u@h',
      sshKey: 'SSH_PRIVATE_KEY',
      release: 'figures',
      namespace: 'apps',
      chart: 'services/figures/helm',
      values: [],
      set: {},
      fromRegistry: true,
      rolloutTimeoutSeconds: 300,
      healthUrl: { url: 'http://figures.apps.svc.cluster.local/health', attempts: 6 },
    });
    const f = fakes();
    const image = {
      repository: 'registry.ovea.pro/qb/figures',
      tag: 'src-0123456789ab',
      digest: 'sha256:' + 'ab'.repeat(32),
    };
    const r = await testHandler(
      step,
      { source, image },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: f.ctx },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { image: 'registry.ovea.pro/qb/figures:src-0123456789ab' },
    });
    expect(f.calls.some((c) => typeof c === 'string' && c.includes(' load'))).toBe(false);
    const remote = sshCmd(f.calls);
    expect(remote[0]).toBe(
      'sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io pull -q registry.ovea.pro/qb/figures:src-0123456789ab',
    );
    expect(remote[2]).toContain(
      '--set image.repository=registry.ovea.pro/qb/figures --set image.tag=src-0123456789ab --set image.pullPolicy=IfNotPresent',
    );
    expect(remote[3]).toBe(
      'kubectl -n apps rollout status deploy/figures --timeout=300s',
    );
    expect(f.calls).toContain('retry 6x5');
  });

  test('imageRefs: registry images next to tarball images, set as repository/tag/digest without a pull', async () => {
    const step = steps.helmDeployOverSsh({
      name: 'deploy-beta',
      description: '',
      host: 'u@h',
      sshKey: KEY,
      release: 'app',
      namespace: 'apps-beta',
      chart: 'helm',
      imageRefs: { harness: 'harness.image' },
    });
    expect(Object.keys(step.input)).toEqual(['source', 'image', 'harness']);
    expect(step.input.image.name).toBe('BuiltImage');
    expect(step.input.harness.name).toBe('PushedImage');
    expect(settingsOf(step)).toMatchObject({ imageRefs: { harness: 'harness.image' } });
    const f = fakes();
    const harness = {
      repository: 'ghcr.io/olivervea/olve-pipelines-harness-base',
      tag: 'src-0123456789ab',
      digest: 'sha256:' + 'cd'.repeat(32),
    };
    const r = await testHandler(
      step,
      { source, image: built('docker.io/library/app', 'app.tar'), harness },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: f.ctx },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { image: 'docker.io/library/app:2026.10.22.3' },
    });
    const remote = sshCmd(f.calls);
    expect(remote.some((c) => c.includes(' pull '))).toBe(false);
    expect(remote[1]).toContain(
      '--set image.pullPolicy=Never --set-string harness.image.repository=ghcr.io/olivervea/olve-pipelines-harness-base ' +
        `--set-string harness.image.tag=src-0123456789ab --set-string harness.image.digest=sha256:${'cd'.repeat(32)}`,
    );
    expect(() =>
      steps.helmDeployOverSsh({
        name: 'd',
        description: '',
        host: 'u@h',
        sshKey: KEY,
        release: 'a',
        namespace: 'n',
        chart: 'helm',
        imageRefs: { image: 'x' },
      }),
    ).toThrow('both in images and imageRefs');
  });

  test('images: {} deploys the chart alone: no image inputs, imports, flags or default rollout', async () => {
    const step = steps.helmDeployOverSsh({
      name: 'deploy',
      description: '',
      host: 'u@h',
      sshKey: KEY,
      release: 'edge',
      namespace: 'apps',
      chart: 'helm',
      images: {},
      values: ['values-prod.yaml'],
    });
    expect(Object.keys(step.input)).toEqual(['source']);
    expect(settingsOf(step)).toMatchObject({ images: {} });
    const f = fakes();
    const r = await testHandler(
      step,
      { source },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: f.ctx },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { release: 'edge', namespace: 'apps' },
    });
    if (r.status === 'succeeded') expect(r.output.image).toBeUndefined();
    expect(f.calls.some((c) => typeof c === 'string' && c.includes(' load'))).toBe(false);
    const remote = sshCmd(f.calls);
    expect(remote.some((c) => c.includes(' pull '))).toBe(false);
    expect(remote[1]).toContain(
      'helm upgrade --install edge . -n apps -f values-prod.yaml',
    );
    expect(remote[1]).not.toContain('--set');
    expect(remote).toHaveLength(2);
    const authentik = steps.helmDeployOverSsh({
      name: 'deploy',
      description: '',
      host: 'u@h',
      sshKey: KEY,
      release: 'authentik',
      namespace: 'apps',
      chart: 'helm',
      images: {},
      deployments: ['authentik-server'],
    });
    const g = fakes();
    await testHandler(
      authentik,
      { source },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: g.ctx },
    );
    expect(sshCmd(g.calls)[2]).toBe(
      'kubectl -n apps rollout status deploy/authentik-server --timeout=120s',
    );
  });

  test("dependencies: Chart.yaml's charts are pulled on the host; atomic: helm waits and rolls back, no rollout waits", async () => {
    const f = fakes();
    writeFileSync(
      join(f.tree, 'helm/Chart.yaml'),
      [
        'name: edge',
        'version: 0.1.0',
        'dependencies:',
        '  - name: cert-manager',
        '    version: "1.21.0"',
        '    repository: https://charts.jetstack.io',
        '    condition: cert-manager.enabled',
        '  - name: tools',
        '    version: 2.0.0',
        '    repository: oci://registry.example/charts/',
      ].join('\n'),
    );
    const step = steps.helmDeployOverSsh({
      name: 'deploy',
      description: '',
      host: 'u@h',
      sshKey: KEY,
      release: 'edge',
      namespace: 'apps',
      chart: 'helm',
      images: {},
      dependencies: true,
      atomic: true,
      rolloutTimeoutSeconds: 600,
    });
    expect(settingsOf(step)).toMatchObject({ dependencies: true, atomic: true });
    expect(
      await testHandler(
        step,
        { source },
        { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: f.ctx },
      ),
    ).toMatchObject({ status: 'succeeded' });
    const remote = sshCmd(f.calls);
    expect(remote.slice(1, 4)).toEqual([
      'mkdir -p /tmp/edge-helm-apps/charts',
      'helm pull cert-manager --repo https://charts.jetstack.io --version 1.21.0 --destination /tmp/edge-helm-apps/charts',
      'helm pull oci://registry.example/charts/tools --version 2.0.0 --destination /tmp/edge-helm-apps/charts',
    ]);
    expect(remote[4]).toContain(
      'helm upgrade --install edge . -n apps --atomic --timeout=600s',
    );
    expect(remote).toHaveLength(5);

    const g = fakes();
    writeFileSync(
      join(g.tree, 'helm/Chart.yaml'),
      'name: x\nversion: 0.1.0\ndependencies:\n  - name: local\n    version: 1.0.0\n    repository: file://../local\n',
    );
    const bad = await testHandler(
      steps.helmDeployOverSsh({
        name: 'd',
        description: '',
        host: 'u@h',
        sshKey: KEY,
        release: 'x',
        namespace: 'n',
        chart: 'helm',
        images: {},
        dependencies: true,
        helmWait: true,
      }),
      { source },
      { secrets: { SSH_PRIVATE_KEY: 'k' }, ctx: g.ctx },
    );
    if (bad.status !== 'failed') throw new Error('expected failure');
    expect(bad.error.message).toContain('an http(s) or oci repository');
  });

  test('bad image config is a definition error', () => {
    expect(() =>
      steps.helmDeployOverSsh({
        name: 'd',
        description: '',
        host: 'u@h',
        sshKey: KEY,
        release: 'a',
        namespace: 'n',
        chart: 'helm',
        deployments: [],
      }),
    ).toThrow('deployments must not be empty');
  });
});

describe('serviceUrl and checkSpa', () => {
  const spa = (opts: { bundle?: boolean; type?: string } = {}) => {
    const urls: string[] = [];
    const ctx = {
      http: {
        expectOk: async (url: string) => {
          urls.push(url);
          if (url.endsWith('/'))
            return new Response(
              opts.bundle === false
                ? '<html></html>'
                : '<script type="module" src="/assets/index-abc.js"></script>',
            );
          return new Response('x', {
            headers: { 'content-type': opts.type ?? 'text/javascript' },
          });
        },
      } as unknown as Ctx['http'],
      log: () => {},
    };
    return { ctx, urls };
  };

  test("serviceUrl is the release's in-cluster Service", () => {
    expect(serviceUrl({ release: 'app', namespace: 'apps-beta' })).toBe(
      'http://app.apps-beta.svc.cluster.local',
    );
  });

  test('checkSpa: the page references an /assets bundle served as JavaScript', async () => {
    const ok = spa();
    expect(await checkSpa(ok.ctx, 'http://app/')).toMatchObject({
      bundle: '/assets/index-abc.js',
    });
    expect(ok.urls).toEqual(['http://app/', 'http://app/assets/index-abc.js']);
    expect(checkSpa(spa({ bundle: false }).ctx, 'http://app')).rejects.toThrow(
      'does not reference an /assets/*.js bundle',
    );
    expect(checkSpa(spa({ type: 'text/html' }).ctx, 'http://app')).rejects.toThrow(
      'is not served as JavaScript',
    );
  });
});

describe('httpHealthCheck', () => {
  const URL = 'http://app.ns.svc.cluster.local/api/health?token=s3cret';
  const fake = (healthyOn: number) => {
    const requests: { url: string; headers?: Record<string, string> }[] = [];
    const retries: { attempts: number; delaySeconds: number }[] = [];
    const logs: string[] = [];
    const ctx = {
      http: {
        expectOk: async (url: string, init?: { headers?: Record<string, string> }) => {
          requests.push({ url, headers: init?.headers });
          if (requests.length < healthyOn)
            throw new Error(`HTTP 503 from ${url.split('?')[0]}`);
          return new Response('ok');
        },
      } as unknown as Ctx['http'],
      retry: (async (opts, fn) => {
        retries.push({ attempts: opts.attempts, delaySeconds: opts.delaySeconds });
        return fastRetry(opts, fn);
      }) as Ctx['retry'],
      log: (m: string, f?: Record<string, unknown>) =>
        void logs.push(`${m} ${JSON.stringify(f ?? {})}`),
    };
    return { ctx, requests, retries, logs };
  };

  test('retries until 2xx, 5 × 5 s by default (v1 parity), and never logs the query', async () => {
    const f = fake(3);
    await httpHealthCheck(f.ctx, URL);
    expect(f.retries).toEqual([{ attempts: 5, delaySeconds: 5 }]);
    expect(f.requests.map((r) => r.url)).toEqual([URL, URL, URL]);
    expect(f.logs.join('\n')).not.toContain('s3cret');
    expect(f.logs.at(-1)).toContain('health OK');
  });

  test('custom attempts, delay and headers; fails with the last error once attempts run out', async () => {
    const f = fake(99);
    const err = await httpHealthCheck(f.ctx, URL, {
      attempts: 2,
      delaySeconds: 1,
      headers: { authorization: 'Bearer t' },
    }).catch((e: Error) => e);
    expect(err).toBeInstanceOf(Error);
    expect((err as Error).message).toContain('HTTP 503');
    expect(f.retries).toEqual([{ attempts: 2, delaySeconds: 1 }]);
    expect(f.requests).toHaveLength(2);
    expect(f.requests[0]!.headers).toEqual({ authorization: 'Bearer t' });
  });
});

describe('library steps in the manifest', () => {
  test('kinds, images, secrets and configs are emitted and schema-valid', () => {
    const Req = model('Req', z.object({ repo: z.string(), sha: z.string() }));
    const p = pipeline({ name: 't', input: Req })
      .stage([
        steps.gitHubSource({ name: 'source', description: '', token: TOKEN }),
        (from) => ({ repo: from.input.repo, sha: from.input.sha }),
      ])
      .stage(steps.imageBuild({ name: 'build', description: '', repository: 'app' }))
      .stage(
        steps.helmDeployOverSsh({
          name: 'deploy',
          description: '',
          host: 'u@h',
          sshKey: KEY,
          release: 'app',
          namespace: 'ns',
          chart: 'helm',
        }),
      );
    const harness = { bun: '1.4.3', sdk: '0.0.0', sdkHash: 'sha256:' + 'a'.repeat(64) };
    const m = describePipeline(p, { handlerNames: [], harness });
    expect(Manifest.safeParse(m).success).toBe(true);
    const all = m.stages.flatMap((s) => ('steps' in s ? s.steps : []));
    expect(all.map((s) => [s.kind, s.image, s.secrets, s.config, s.input])).toEqual([
      [
        'gitHubSource',
        'alpine:3.22',
        ['GITHUB_TOKEN'],
        { token: 'GITHUB_TOKEN' },
        { repo: { ref: 'input.repo' }, sha: { ref: 'input.sha' } },
      ],
      [
        'imageBuild',
        'gcr.io/kaniko-project/executor:debug',
        [],
        { repository: 'app' },
        { source: { ref: 'source' } },
      ],
      [
        'helmDeployOverSsh',
        'alpine:3.22',
        ['SSH_PRIVATE_KEY'],
        {
          host: 'u@h',
          sshKey: 'SSH_PRIVATE_KEY',
          release: 'app',
          namespace: 'ns',
          chart: 'helm',
          values: [],
          set: {},
        },
        { source: { ref: 'source' }, image: { ref: 'build' } },
      ],
    ]);
  });
});
