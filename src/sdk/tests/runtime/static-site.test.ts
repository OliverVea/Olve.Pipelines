// steps.staticSite: the generated image context, the bundled chart's deploys and a pull request's preview.
// Run through testHandler with faked ctx parts; the assertions pin the exact commands and generated files.
import { afterAll, describe, expect, test } from 'bun:test';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, relative } from 'node:path';
import {
  checks,
  config,
  helmTarget,
  implement,
  model,
  pipeline,
  secret,
  Site,
  siteIngress,
  step,
  steps,
  z,
  type Ctx,
  type FileRef,
} from '../../src/index.ts';
import { recordConfigRefs } from '../../src/core/config-refs.ts';
import { describePipeline } from '../../src/describe/describe.ts';
import { Manifest } from '../../src/describe/manifest-schema.ts';
import { SERVICE_ACCOUNT_TOKEN } from '../../src/library/helm-deploy.ts';
import { KANIKO_IMAGE } from '../../src/library/image-build.ts';
import {
  CADDY_IMAGE,
  SITE_CHART,
  siteCaddyfile,
  siteDockerfile,
} from '../../src/library/static-site-assets.ts';
import { defaultPreviewPrefix } from '../../src/library/static-site.ts';
import { testFileRef, testHandler } from '../../src/testing/index.ts';
import { tmp, removeTmpDirs } from './helpers.ts';

afterAll(removeTmpDirs);

const KEY = secret('SSH_PRIVATE_KEY', 'key');
const KUBECONFIG = secret('PREVIEW_KUBECONFIG', 'kubeconfig');
const DIGEST = 'sha256:' + 'ab'.repeat(32);
const image = {
  repository: 'registry.example.com/web',
  tag: '2026.11.17.5',
  digest: DIGEST,
};
const ci = (o: Partial<{ kind: 'pull' | 'queue' | 'close'; pull: number }> = {}) => ({
  repo: 'OliverVea/Olve.Trains',
  sha: '0123456789abcdef0123456789abcdef01234567',
  kind: o.kind ?? 'pull',
  pull: o.pull ?? 12,
});

const site = (o: { wasm?: boolean; spa?: boolean; dir?: string } = {}) =>
  steps.staticSite({ name: 'web', repository: 'registry.example.com/web', ...o });

describe('steps.staticSite: the Caddyfile', () => {
  const caddyfile = (tryFiles: string, scriptSrc: string) => `{
	admin off
	persist_config off
	auto_https off
	storage_check off
	storage_clean_interval off
	servers {
		protocols h1
	}
}

:8080 {
	root * /srv
${tryFiles}	file_server {
		precompressed br gzip
	}
	header {
		-Server
		X-Content-Type-Options nosniff
		Referrer-Policy no-referrer
		Cross-Origin-Opener-Policy same-origin
		Cache-Control no-cache
		Content-Security-Policy "default-src 'self'; script-src ${scriptSrc}; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'"
	}
}
`;
  const spaLine = '\ttry_files {path} {path}/ /index.html\n';
  test.each([
    [false, false, '', "'self'"],
    [true, false, '', "'self' 'wasm-unsafe-eval'"],
    [false, true, spaLine, "'self'"],
    [true, true, spaLine, "'self' 'wasm-unsafe-eval'"],
  ])('wasm %p, spa %p', (wasm, spa, tryFiles, scriptSrc) => {
    expect(siteCaddyfile({ wasm, spa })).toBe(caddyfile(tryFiles, scriptSrc));
  });
});

describe('steps.staticSite: the image', () => {
  const fakeKaniko = (layout: Record<string, string>) => {
    const root = tmp('site-root');
    for (const [path, text] of Object.entries(layout)) {
      mkdirSync(join(root, path, '..'), { recursive: true });
      writeFileSync(join(root, path), text);
    }
    const calls: string[][] = [];
    const exec: Ctx['exec'] = async (argv) => {
      calls.push([...argv]);
      if (argv[0] === '/kaniko/executor') {
        const digestFile = argv.find((a) => a.startsWith('--digest-file='))!.slice(14);
        writeFileSync(digestFile, DIGEST + '\n');
      }
    };
    const fetched: FileRef[] = [];
    const files = { fetch: async (ref: FileRef) => (fetched.push(ref), root) };
    const http = {
      fetch: async () => new Response('{}', { status: 401 }),
    } as unknown as Ctx['http'];
    return { root, calls, fetched, ctx: { exec, files, http } };
  };
  const listing = (dir: string): Record<string, string> =>
    Object.fromEntries(
      readdirSync(dir, { recursive: true, encoding: 'utf8' })
        .filter((p) => statSync(join(dir, p)).isFile())
        .sort()
        .map((p) => [p, readFileSync(join(dir, p), 'utf8')]),
    );
  const root = testFileRef({ step: 'build-web', path: 'wwwroot/' });

  test('the context is the Dockerfile, the Caddyfile and the site as wwwroot; kaniko builds and pushes it', async () => {
    const s = site({ wasm: true });
    expect(s.image).toMatchObject({
      name: 'web-image',
      kind: 'imageBuild',
      image: KANIKO_IMAGE,
      retries: 2,
      secrets: [],
    });
    expect(s.image.output!.name).toBe('PushedImage');
    expect(recordConfigRefs(s.image.settings)).toEqual({
      repository: 'registry.example.com/web',
      push: true,
      site: { wasm: true, spa: false },
    });
    const k = fakeKaniko({
      'index.html': '<h1>hi</h1>',
      'index.html.br': 'br',
      '_framework/app.wasm': 'wasm',
    });
    const r = await testHandler(
      s.image,
      { site: { root } },
      { ctx: k.ctx, run: { version: '2026.11.17.5' } },
    );
    expect(r).toMatchObject({ status: 'succeeded', output: { ...image, cached: false } });
    expect(k.fetched).toEqual([root]);
    const context = k.calls[1]![2]!.replace(/\/\.$/, '');
    expect(relative(join(r.dir, 'tmp'), context)).toMatch(/^site-[^/]+$/);
    expect(listing(context)).toEqual({
      Caddyfile: siteCaddyfile({ wasm: true, spa: false }),
      Dockerfile: siteDockerfile(),
      'wwwroot/_framework/app.wasm': 'wasm',
      'wwwroot/index.html': '<h1>hi</h1>',
      'wwwroot/index.html.br': 'br',
    });
    const digestFile = k.calls[2]!.find((a) => a.startsWith('--digest-file='))!;
    expect(k.calls).toEqual([
      ['mkdir', '-p', '/kaniko/build-context'],
      ['cp', '-r', `${context}/.`, '/kaniko/build-context'],
      [
        '/kaniko/executor',
        '--context=/kaniko/build-context',
        '--dockerfile=/kaniko/build-context/Dockerfile',
        digestFile,
        '--destination=registry.example.com/web:2026.11.17.5',
        '--snapshot-mode=redo',
        '--use-new-run',
        '--compressed-caching=false',
        '--cache=true',
        '--cache-repo=registry.example.com/web/cache',
      ],
    ]);
  });

  test('the Dockerfile copies the pinned Caddy binary onto an empty image', () => {
    expect(siteDockerfile()).toBe(`FROM ${CADDY_IMAGE} AS caddy

# A plain copy drops the binary's cap_net_bind_service file capability: it listens on 8080.
RUN cp /usr/bin/caddy /caddy

FROM scratch
COPY --from=caddy /caddy /caddy
COPY Caddyfile /etc/caddy/Caddyfile
COPY wwwroot /srv
USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["/caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
`);
    expect(CADDY_IMAGE).toMatch(/^caddy:[0-9.]+@sha256:[0-9a-f]{64}$/);
  });

  test('dir: the site is a subdirectory of the root (a source tree)', async () => {
    const k = fakeKaniko({ 'site/index.html': 'page', 'README.md': 'not served' });
    const r = await testHandler(
      site({ dir: './site/' }).image,
      { site: { root } },
      { ctx: k.ctx },
    );
    expect(r.status).toBe('succeeded');
    expect(listing(k.calls[1]![2]!.replace(/\/\.$/, ''))).toMatchObject({
      'wwwroot/index.html': 'page',
    });
    expect(Object.keys(listing(k.calls[1]![2]!.replace(/\/\.$/, '')))).not.toContain(
      'wwwroot/README.md',
    );
  });

  test.each([
    [
      'no index.html',
      {},
      { 'app.js': '' },
      "step 'web-image' failed: the site has no index.html",
    ],
    [
      'no index.html in dir',
      { dir: 'site' },
      { 'site/app.js': '' },
      "step 'web-image' failed: the site has no index.html (in 'site')",
    ],
    [
      'a missing dir',
      { dir: 'public' },
      { 'index.html': '' },
      "step 'web-image' failed: 'public' is not a directory of the site root",
    ],
  ])('%s fails the step before kaniko', async (_, o, layout, message) => {
    const k = fakeKaniko(layout);
    const r = await testHandler(site(o).image, { site: { root } }, { ctx: k.ctx });
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'handler', message } });
    expect(k.calls).toEqual([]);
  });
});

describe('steps.staticSite: deploys', () => {
  const remote = (calls: (string[] | string)[]) =>
    calls
      .filter((c): c is string[] => Array.isArray(c) && c[0] === 'ssh')
      .map((c) => c.at(-1)!);
  const fakes = () => {
    const calls: (string[] | string)[] = [];
    let chart = '';
    const ctx: Partial<Ctx<'SSH_PRIVATE_KEY'>> = {
      exec: async (argv) => {
        calls.push([...argv]);
        if (argv[0] === 'scp') chart = argv.at(-2)!;
      },
      sh: Object.assign(
        async (cmd: string | { script: string }) => void calls.push(cmd as string),
        {
          text: async () => '',
        },
      ),
      http: {
        expectOk: async (url: string) => (calls.push(`GET ${url}`), new Response('ok')),
      } as unknown as Ctx['http'],
    };
    return { calls, ctx, chart: () => chart };
  };
  const target = {
    kind: 'ssh',
    host: 'deploy@host.example.com',
    hostKey: false,
    namespace: 'apps',
    release: 'web',
    values: [],
    set: {},
  } as const;

  test.each([
    ['without an ingress', undefined, undefined],
    [
      'with an ingress',
      {
        host: 'web.example.com',
        tlsSecret: 'web-tls',
        annotations: { 'example.com/a': 'b' },
      },
      ' -f olve-values-0.json',
    ],
  ])(
    'ssh target %s: the bundled chart is copied and upgraded with the image by digest',
    async (_, ingress, valuesFlag) => {
      const deploy = site().deploy({
        name: 'deploy-web',
        target,
        sshKey: KEY,
        ingress,
        healthPath: '/',
      });
      expect(deploy).toMatchObject({ kind: 'helmDeployOverSsh', secrets: [KEY] });
      expect(Object.keys(deploy.input)).toEqual(['image']);
      const f = fakes();
      const r = await testHandler(
        deploy,
        { image },
        { ctx: f.ctx, secrets: { SSH_PRIVATE_KEY: 'k' } },
      );
      expect(r).toMatchObject({
        status: 'succeeded',
        output: {
          release: 'web',
          namespace: 'apps',
          image: `${image.repository}:${image.tag}@${DIGEST}`,
        },
      });
      const files = readdirSync(f.chart(), { recursive: true, encoding: 'utf8' })
        .filter((p) => statSync(join(f.chart(), p)).isFile())
        .sort();
      expect(files).toEqual(
        [...Object.keys(SITE_CHART), ...(ingress ? ['olve-values-0.json'] : [])].sort(),
      );
      if (ingress)
        expect(
          JSON.parse(readFileSync(join(f.chart(), 'olve-values-0.json'), 'utf8')),
        ).toEqual({
          ingress,
        });
      const chart = '/tmp/web-helm-apps';
      expect(remote(f.calls)).toEqual([
        `sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io pull -q ${image.repository}@${DIGEST}`,
        `rm -rf ${chart}`,
        `cd ${chart} && helm upgrade --install web . -n apps${valuesFlag ?? ''} ` +
          `--set image.repository=${image.repository} --set image.tag=${image.tag} --set image.pullPolicy=IfNotPresent ` +
          `--set-string image.digest=${DIGEST} && cd / && rm -rf ${chart}`,
        'kubectl -n apps rollout status deploy/web --timeout=120s',
      ]);
      expect(f.calls.at(-1)).toBe('GET http://web.apps.svc.cluster.local/');
    },
  );

  test('inCluster target: helm --wait in the step pod, from the bundled chart', async () => {
    const deploy = site().deploy({
      name: 'deploy-web',
      target: {
        kind: 'inCluster',
        namespace: 'apps',
        release: 'web',
        values: [],
        set: {},
      },
    });
    const run = (calls: { argv: string[]; cwd?: string }[]) =>
      testHandler(
        deploy,
        { image },
        {
          ctx: {
            exec: async (argv, o) => void calls.push({ argv: [...argv], cwd: o?.cwd }),
          },
        },
      );
    if (!existsSync(SERVICE_ACCOUNT_TOKEN)) {
      const r = await run([]);
      expect(r).toMatchObject({ status: 'failed' });
      if (r.status === 'failed')
        expect(r.error.message).toContain('no ServiceAccount token in the step pod');
      try {
        mkdirSync(dirname(SERVICE_ACCOUNT_TOKEN), { recursive: true });
        writeFileSync(SERVICE_ACCOUNT_TOKEN, 'token');
      } catch {
        return; // not root: the success path runs where the token path is writable (the Linux test container)
      }
    }
    const calls: { argv: string[]; cwd?: string }[] = [];
    const r = await run(calls);
    expect(r).toMatchObject({ status: 'succeeded', output: { release: 'web' } });
    expect(calls.map((c) => c.argv)).toEqual([
      [
        'helm',
        'upgrade',
        '--install',
        'web',
        '.',
        '-n',
        'apps',
        '--set',
        `image.repository=${image.repository}`,
        '--set',
        `image.tag=${image.tag}`,
        '--set',
        'image.pullPolicy=IfNotPresent',
        '--set-string',
        `image.digest=${DIGEST}`,
        '--wait',
        '--timeout=120s',
      ],
    ]);
    expect(readFileSync(join(calls[0]!.cwd!, 'Chart.yaml'), 'utf8')).toBe(
      SITE_CHART['Chart.yaml']!,
    );
  });

  test('from config: an in-cluster target runs helm in the step pod; values files are refused', async () => {
    const WEB = config(
      'web',
      'Where the site is deployed.',
      z.object({
        beta: helmTarget({ release: 'web' }, { ingress: siteIngress().optional() }),
      }),
    );
    const s = site();
    const inCluster = s.deploy({ name: 'deploy-web-beta', target: WEB.beta as never });
    expect(inCluster).toMatchObject({
      kind: 'helmDeployInCluster',
      image: 'alpine/helm:3.16.2',
      clusterAccess: true,
      secrets: [],
    });
    expect(recordConfigRefs(inCluster.settings)).toEqual({
      target: { $config: 'web.beta' },
      chart: 'static-site',
      images: { image: 'image' },
    });
    const ssh = s.deploy({
      name: 'deploy-web-beta',
      target: WEB.beta as never,
      sshKey: KEY,
      ingress: WEB.beta.ingress as never,
    });
    const r = await testHandler(
      ssh,
      { image },
      {
        ctx: fakes().ctx,
        secrets: { SSH_PRIVATE_KEY: 'k' },
        config: { web: { beta: { ...target, values: ['values-beta.yaml'] } } },
      },
    );
    expect(r).toMatchObject({
      status: 'failed',
      error: {
        message:
          "step 'deploy-web-beta' failed: helmDeployOverSsh 'deploy-web-beta': the target's values files can't apply to the bundled chart (values-beta.yaml); use set",
      },
    });
  });
});

describe('steps.staticSite: previews', () => {
  const fakes = () => {
    const calls: { argv: string[]; cwd?: string; env?: Record<string, string> }[] = [];
    const ctx: Partial<Ctx<'PREVIEW_KUBECONFIG'>> = {
      exec: async (argv, o) =>
        void calls.push({ argv: [...argv], cwd: o?.cwd, env: o?.env }),
    };
    return { calls, ctx };
  };
  const config_ = (o: Record<string, unknown> = {}) => ({
    preview: {
      namespace: 'previews',
      domain: 'preview.example.com',
      tlsSecret: 'preview-wildcard',
      ...o,
    },
  });
  const PREVIEW = config(
    'preview',
    'Where pull requests are previewed.',
    z.object({
      namespace: z.string(),
      domain: z.string(),
      tlsSecret: z.string().optional(),
      annotations: z.record(z.string(), z.string()).optional(),
    }),
  );
  const preview = (release?: string) =>
    site().preview({
      kubeconfig: KUBECONFIG,
      namespace: PREVIEW.namespace,
      domain: PREVIEW.domain,
      tlsSecret: PREVIEW.tlsSecret,
      annotations: PREVIEW.annotations,
      ...(release ? { release } : {}),
    });
  const kubeconfig = 'apiVersion: v1\nkind: Config\n';

  test('deploy: helm upgrades the release of the pull request at pr-<n>.<domain> and links it; remove uninstalls the same release', async () => {
    const { deploy, remove } = preview('trains-preview');
    expect(deploy).toMatchObject({
      name: 'web-preview',
      image: 'alpine/helm:3.16.2',
      reuse: false,
      secrets: [KUBECONFIG],
    });
    expect(remove).toMatchObject({
      name: 'web-preview-remove',
      reuse: false,
      secrets: [KUBECONFIG],
    });
    const f = fakes();
    const r = await testHandler(
      deploy,
      { image, ci: ci() },
      {
        ctx: f.ctx,
        secrets: { PREVIEW_KUBECONFIG: kubeconfig },
        config: config_({
          annotations: { 'external-dns.alpha.kubernetes.io/controller': 'none' },
        }),
      },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: {
        release: 'trains-preview-12',
        namespace: 'previews',
        image: `${image.repository}:${image.tag}@${DIGEST}`,
        url: 'https://pr-12.preview.example.com',
      },
      links: [{ url: 'https://pr-12.preview.example.com', label: 'preview' }],
    });
    const [helm] = f.calls;
    expect(helm!.argv).toEqual([
      'helm',
      'upgrade',
      '--install',
      'trains-preview-12',
      '.',
      '-n',
      'previews',
      '-f',
      'olve-preview-values.json',
      '--set-string',
      `image.repository=${image.repository}`,
      '--set-string',
      `image.tag=${image.tag}`,
      '--set-string',
      `image.digest=${DIGEST}`,
      '--set-string',
      'preview=trains-preview',
      '--set-string',
      'ingress.host=pr-12.preview.example.com',
      '--set-string',
      'ingress.tlsSecret=preview-wildcard',
      '--wait',
      '--timeout=300s',
    ]);
    expect(
      JSON.parse(readFileSync(join(helm!.cwd!, 'olve-preview-values.json'), 'utf8')),
    ).toEqual({
      ingress: { annotations: { 'external-dns.alpha.kubernetes.io/controller': 'none' } },
    });
    expect(readFileSync(join(helm!.cwd!, 'Chart.yaml'), 'utf8')).toBe(
      SITE_CHART['Chart.yaml']!,
    );
    const kubeFile = helm!.env!.KUBECONFIG!;
    expect(readFileSync(kubeFile, 'utf8')).toBe(kubeconfig);
    expect(statSync(kubeFile).mode & 0o777).toBe(0o600);

    const g = fakes();
    const removed = await testHandler(
      remove,
      { ci: ci({ kind: 'close' }) },
      { ctx: g.ctx, secrets: { PREVIEW_KUBECONFIG: kubeconfig }, config: config_() },
    );
    expect(removed.status).toBe('succeeded');
    expect(g.calls.map((c) => c.argv)).toEqual([
      [
        'helm',
        'uninstall',
        'trains-preview-12',
        '-n',
        'previews',
        '--ignore-not-found',
        '--wait',
      ],
    ]);
    expect(readFileSync(g.calls[0]!.env!.KUBECONFIG!, 'utf8')).toBe(kubeconfig);
    for (const c of [...f.calls, ...g.calls])
      expect(c.argv.some((a) => a.includes('kind: Config'))).toBe(false);
  });

  test('the default release is named after the repository and the site; no TLS secret serves plain HTTP', async () => {
    const f = fakes();
    const r = await testHandler(
      preview().deploy,
      { image, ci: ci({ pull: 7 }) },
      {
        ctx: f.ctx,
        secrets: { PREVIEW_KUBECONFIG: kubeconfig },
        config: config_({ tlsSecret: undefined }),
      },
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: {
        release: 'olve-trains-web-preview-7',
        url: 'http://pr-7.preview.example.com',
      },
    });
    const argv = f.calls[0]!.argv;
    expect(argv.slice(3, 7)).toEqual([
      'olve-trains-web-preview-7',
      '.',
      '-n',
      'previews',
    ]);
    expect(argv).toContain('preview=olve-trains-web-preview');
    expect(argv.some((a) => a.startsWith('ingress.tlsSecret') || a === '-f')).toBe(false);
    expect(defaultPreviewPrefix('OliverVea/Olve.Trains', 'web')).toBe(
      'olve-trains-web-preview',
    );
  });

  test.each([
    [
      'a queue run',
      { kind: 'queue' as const, pull: 0 },
      {},
      "step 'web-preview' failed: a preview is deployed by a pull request's run, not a queue run (stage it in when({ kind: 'pull' }, …))",
    ],
    [
      'a domain that is not a host name',
      {},
      { domain: 'https://preview.example.com/' },
      "step 'web-preview' failed: domain 'https://preview.example.com/' is not a host name",
    ],
  ])('%s fails the deploy before helm', async (_, run, cfg, message) => {
    const f = fakes();
    const r = await testHandler(
      preview().deploy,
      { image, ci: ci(run) },
      { ctx: f.ctx, secrets: { PREVIEW_KUBECONFIG: kubeconfig }, config: config_(cfg) },
    );
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'handler', message } });
    expect(f.calls).toEqual([]);
  });

  test('an unset kubeconfig fails the step', async () => {
    const r = await testHandler(
      preview().remove,
      { ci: ci({ kind: 'close' }) },
      { ctx: fakes().ctx, config: config_() },
    );
    expect(r).toMatchObject({
      status: 'failed',
      error: {
        message:
          "step 'web-preview-remove' failed: secret PREVIEW_KUBECONFIG is not set (the harness reads it from the environment variable PREVIEW_KUBECONFIG)",
      },
    });
  });
});

describe('steps.staticSite: definitions', () => {
  test.each<[string, () => unknown, string]>([
    [
      'a dir outside the root',
      () => site({ dir: '../x' }),
      "staticSite 'web': dir '../x' must be a relative path inside the site root",
    ],
    [
      'an absolute dir',
      () => site({ dir: '/srv' }),
      "staticSite 'web': dir '/srv' must be a relative path inside the site root",
    ],
    [
      'a push without a registry host',
      () => steps.staticSite({ name: 'web', repository: 'web' }),
      "imageBuild 'web-image': push needs a repository with a registry host, got 'web'",
    ],
    [
      'a preview release that is not a DNS label',
      () =>
        site().preview({
          kubeconfig: KUBECONFIG,
          namespace: 'previews',
          domain: 'preview.example.com',
          release: 'Trains_Preview',
        }),
      "staticSite 'web': preview release 'Trains_Preview' must be a DNS label of at most 42 characters",
    ],
    [
      'a relative healthPath',
      () =>
        site().deploy({
          name: 'deploy-web',
          target: {
            kind: 'inCluster',
            namespace: 'n',
            release: 'web',
            values: [],
            set: {},
          },
          healthPath: 'health',
        }),
      "helmDeployInCluster 'deploy-web': healthPath must start with '/'",
    ],
  ])('%s', (_, make, message) => {
    expect(make).toThrow(message);
  });
});

describe('steps.staticSite in the manifest', () => {
  test('inputs are matched by model: Site, PushedImage and the CI input', () => {
    const buildWeb = implement(
      step({ name: 'build-web', description: '', image: 'alpine', output: Site }),
      async (_, ctx) => ({ root: await ctx.output.dir('/out', { name: 'wwwroot' }) }),
    );
    const PREVIEW = config(
      'preview',
      'Where pull requests are previewed.',
      z.object({ namespace: z.string(), domain: z.string() }),
    );
    const s = site({ spa: true });
    const { deploy, remove } = s.preview({
      kubeconfig: KUBECONFIG,
      namespace: PREVIEW.namespace,
      domain: PREVIEW.domain,
    });
    const WEB = config(
      'web',
      'Where the site is deployed.',
      z.object({
        beta: helmTarget({ release: 'web' }, { ingress: siteIngress().optional() }),
      }),
    );
    const cd = pipeline({
      name: 'site',
      input: model('Req', z.object({ sha: z.string() })),
    })
      .stage(buildWeb)
      .stage(s.image)
      .select(WEB.beta, {
        ssh: (q, beta) =>
          q.stage(
            s.deploy({
              name: 'deploy-web-beta',
              target: beta,
              sshKey: KEY,
              ingress: beta.ingress,
            }),
          ),
        inCluster: (q, beta) =>
          q.stage(
            s.deploy({ name: 'deploy-web-beta', target: beta, ingress: beta.ingress }),
          ),
      });
    const c = checks({ secrets: [KUBECONFIG] })
      .stage(buildWeb)
      .when({ kind: 'pull' }, (q) => q.stage(s.image).stage(deploy))
      .onClose(remove);
    const harness = { bun: '1.4.3', sdk: '0.0.0', sdkHash: 'sha256:' + 'a'.repeat(64) };
    const m = describePipeline(cd, { harness, ci: c }) as any;
    expect(Manifest.safeParse(m).success).toBe(true);
    const named = (steps: any[]) => steps.map((d: any) => [d.name, d.input]);
    expect(named([m.stages[0].steps[0], m.stages[1].steps[0]])).toEqual([
      ['build-web', {}],
      ['web-image', { site: { ref: 'build-web' } }],
    ]);
    const deployOf = (kind: string) => m.stages[2].cases[kind][0].steps[0];
    expect(
      ['ssh', 'inCluster'].map((k) => [
        deployOf(k).kind,
        deployOf(k).input,
        deployOf(k).config.inlineValues,
      ]),
    ).toEqual([
      [
        'helmDeployOverSsh',
        { image: { ref: 'web-image' } },
        [{ ingress: { $config: 'web.beta.ingress', $case: 'web.beta.kind=ssh' } }],
      ],
      [
        'helmDeployInCluster',
        { image: { ref: 'web-image' } },
        [{ ingress: { $config: 'web.beta.ingress', $case: 'web.beta.kind=inCluster' } }],
      ],
    ]);
    expect(named(m.ci.stages[1].stages.flatMap((st: any) => st.steps))).toEqual([
      ['web-image', { site: { ref: 'build-web' } }],
      ['web-preview', { image: { ref: 'web-image' }, ci: { ref: 'input' } }],
    ]);
    expect(
      m.ci.onClose.steps.map((d: any) => [d.name, d.input, d.reuse, d.config]),
    ).toEqual([
      [
        'web-preview-remove',
        { ci: { ref: 'input' } },
        false,
        { namespace: { $config: 'preview.namespace' } },
      ],
    ]);
  });
});
