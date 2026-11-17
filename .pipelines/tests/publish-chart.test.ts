import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { testFileRef, testHandler } from '@olivervea/pipelines/testing';
import { chartRegistry, chartVersion, publishChart } from '../steps/publish-chart';

const repo = join(import.meta.dir, '../..');
const source = {
  repo: 'o/r',
  sha: 'abc',
  tree: testFileRef({ step: 'source', path: 'tree' }),
};
const server = {
  repository: 'registry.example.com/olve-pipelines',
  tag: '2026.01.05.12',
  digest: 'sha256:1',
};
const publish = (release: object[]) => ({
  publish: {
    server: { repository: server.repository, release },
    harness: { repository: 'r/h' },
  },
});

function fakes() {
  const calls: {
    argv: readonly string[];
    env: Record<string, string>;
    values?: string;
  }[] = [];
  const ctx = {
    files: { fetch: async () => repo },
    exec: async (argv: readonly string[], opts?: { env?: Record<string, string> }) => {
      const env = opts?.env ?? {};
      calls.push({
        argv,
        env,
        values:
          argv[1] === 'package'
            ? readFileSync(join(argv[2]!, 'values.yaml'), 'utf8')
            : env.HELM_REGISTRY_CONFIG && readFileSync(env.HELM_REGISTRY_CONFIG, 'utf8'),
      });
    },
  };
  return { ctx, calls };
}

// The same rows as src/cli/test/settings.test.ts: pl installs the chart its own release published.
test.each([
  ['2026.11.17.296', '20261117.296.0'],
  ['2026.01.05.12', '20260105.12.0'],
  ['2027.12.31.1000', '20271231.1000.0'],
])('release %s is chart version %s', (release, chart) => {
  expect(chartVersion(release)).toBe(chart);
});

describe('publish-chart', () => {
  test("packages the chart as the run's release, defaulting to its image, and pushes it beside the release repository", async () => {
    const f = fakes();
    const r = await testHandler(
      publishChart,
      { source, server },
      {
        ctx: f.ctx,
        run: { version: '2026.01.05.12' },
        secrets: { GHCR_TOKEN: 'token' },
        config: publish([
          {
            repository: 'ghcr.io/you/olve-pipelines',
            auth: { username: 'you', password: { secret: 'GHCR_TOKEN' } },
          },
        ]),
      },
    );
    expect(r.status).toBe('succeeded');
    const [pack, push] = f.calls;
    expect(pack!.argv.slice(3)).toEqual([
      '--version',
      '20260105.12.0',
      '--app-version',
      '2026.01.05.12',
      '-d',
      expect.stringMatching(/chart-0\/out$/),
    ]);
    expect(pack!.values).toContain('image:\n  repository: ghcr.io/you/olve-pipelines\n');
    expect(push!.argv).toEqual([
      'helm',
      'push',
      expect.stringMatching(/chart-0\/out\/olve-pipelines-20260105\.12\.0\.tgz$/),
      'oci://ghcr.io/you/charts',
    ]);
    expect(JSON.parse(push!.values!)).toEqual({
      auths: { 'ghcr.io': { auth: Buffer.from('you:token').toString('base64') } },
    });
  });

  test('an insecure release registry is pushed to over plain HTTP', async () => {
    const f = fakes();
    await testHandler(
      publishChart,
      { source, server },
      {
        ctx: f.ctx,
        run: { version: '2026.01.05.12' },
        config: publish([{ repository: 'registry:5000/olve-pipelines', insecure: true }]),
      },
    );
    expect(f.calls[1]!.argv.slice(3)).toEqual([
      'oci://registry:5000/charts',
      '--plain-http',
    ]);
  });

  test('no release repositories publish no chart', async () => {
    const f = fakes();
    const r = await testHandler(
      publishChart,
      { source, server },
      { ctx: f.ctx, run: { version: '2026.01.05.12' }, config: publish([]) },
    );
    expect(r.status).toBe('succeeded');
    expect(f.calls).toHaveLength(0);
    expect(r.logs.join('\n')).toContain(
      'no release repositories (publish.server.release): no chart published',
    );
  });

  test('a run version that is no release fails', async () => {
    const r = await testHandler(
      publishChart,
      { source, server },
      {
        ctx: fakes().ctx,
        config: publish([{ repository: 'ghcr.io/you/olve-pipelines' }]),
      },
    );
    expect(r).toMatchObject({
      status: 'failed',
      error: {
        kind: 'handler',
        message:
          "step 'publish-chart' failed: '0.0.0-test' is not a release version (yyyy.MM.dd.<run>)",
      },
    });
  });

  test('a release repository without a path has nowhere for the chart', () => {
    expect(() => chartRegistry('olve-pipelines')).toThrow(
      "release repository 'olve-pipelines' has no registry path",
    );
  });
});
