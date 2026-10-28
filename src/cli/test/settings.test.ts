import { describe, expect, test } from 'bun:test';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { ProcessResult, ProcessSpec } from '../src/process';
import { ID, fakeFetch, health, pl } from './helpers';

const alpha = {
  id: ID,
  name: 'alpha',
  description: null,
  repo: null,
  health: health(),
  stages: [],
};

/** Answers `/api/pipelines` with alpha and everything else with `body`. */
const api = (body: unknown = {}, status = 200) =>
  fakeFetch((req) =>
    req.path === '/api/pipelines' ? { body: [alpha] } : { body, status },
  );

describe('secret', () => {
  test('set sends stdin verbatim, warns about a lone trailing newline, and --trim drops it', async () => {
    const { fetch, requests } = api();
    const piped = await pl(['secret', 'set', 'alpha', 'TOKEN'], {
      fetch,
      stdin: 'abc\n',
    });
    expect(piped.code).toBe(0);
    expect(requests.at(-1)!.method).toBe('PUT');
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/secrets/TOKEN`);
    expect(requests.at(-1)!.body).toEqual({ value: 'abc\n' });
    expect(piped.stderr).toContain('ends with a newline');
    expect(piped.stdout).toBe("Set secret 'TOKEN' for pipeline alpha.\n");
    await pl(['secret', 'set', 'alpha', 'TOKEN', '--trim'], { fetch, stdin: 'abc\r\n' });
    expect(requests.at(-1)!.body).toEqual({ value: 'abc' });
    const pem = '-----BEGIN-----\nx\n-----END-----\n';
    const multi = await pl(['secret', 'set', 'alpha', 'KEY'], { fetch, stdin: pem });
    expect(requests.at(-1)!.body).toEqual({ value: pem });
    expect(multi.stderr).not.toContain('newline');
  });

  test('set reads --from-env and --from-file, refuses both, and prompts hidden on a terminal', async () => {
    const { fetch, requests } = api();
    await pl(['secret', 'set', 'alpha', 'A', '--from-env', 'MY_TOKEN'], {
      fetch,
      env: { MY_TOKEN: 't' },
    });
    expect(requests.at(-1)!.body).toEqual({ value: 't' });
    const unset = await pl(['secret', 'set', 'alpha', 'A', '--from-env', 'NOPE'], {
      fetch,
    });
    expect(unset.stderr).toContain("Environment variable 'NOPE' is not set.");
    const file = join(mkdtempSync(join(tmpdir(), 'pl-secret-')), 'key');
    writeFileSync(file, 'k\n');
    await pl(['secret', 'set', 'alpha', 'A', '--from-file', file], { fetch });
    expect(requests.at(-1)!.body).toEqual({ value: 'k\n' });
    const both = await pl(
      ['secret', 'set', 'alpha', 'A', '--from-env', 'X', '--from-file', file],
      { fetch },
    );
    expect(both.stderr).toContain('only one of');
    const prompts: string[] = [];
    await pl(['secret', 'set', 'alpha', 'A'], {
      fetch,
      stdinIsTerminal: true,
      prompt: async (q) => (prompts.push(q), 'typed'),
    });
    expect(prompts).toEqual(["Value for 'A': "]);
    expect(requests.at(-1)!.body).toEqual({ value: 'typed' });
  });

  test('delete passes --force; copy sends the source and refuses --name for a whole pipeline', async () => {
    const { fetch, requests } = api({ copied: ['GITHUB_TOKEN'] });
    await pl(['secret', 'delete', 'alpha', 'A', '--force'], { fetch });
    expect(requests.at(-1)!.method).toBe('DELETE');
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/secrets/A?force=true`);
    const copied = await pl(
      ['secret', 'copy', 'alpha/GITHUB_TOKEN', 'alpha', '--name', 'GH'],
      { fetch },
    );
    expect(requests.at(-1)!.body).toEqual({
      sourcePipelineId: ID,
      names: ['GITHUB_TOKEN'],
      targetName: 'GH',
      overwrite: false,
    });
    expect(copied.stdout).toBe('Copied GITHUB_TOKEN from alpha to alpha.\n');
    const whole = await pl(['secret', 'copy', 'alpha', 'alpha', '--name', 'GH'], {
      fetch,
    });
    expect(whole.code).toBe(1);
    expect(whole.stderr).toContain('--name needs a single source secret');
  });
});

describe('binding', () => {
  test('create sends the repo and options; unset options are null', async () => {
    const { fetch, requests } = api({});
    await pl(
      [
        'binding',
        'create',
        'OliverVea/app',
        '--credentials-secret',
        'GITHUB_TOKEN',
        '--json',
      ],
      { fetch },
    );
    expect(requests.at(-1)!.method).toBe('POST');
    expect(requests.at(-1)!.body).toEqual({
      repo: 'OliverVea/app',
      branch: null,
      path: null,
      credentialsSecret: 'GITHUB_TOKEN',
    });
  });

  test('reconcile passes --rebuild only when given', async () => {
    const { fetch, requests } = api({});
    const queued = await pl(['binding', 'reconcile', 'alpha', '--rebuild'], { fetch });
    expect(requests.at(-1)!.method).toBe('POST');
    expect(requests.at(-1)!.path).toBe(
      `/api/pipelines/${ID}/binding/reconcile?rebuild=true`,
    );
    expect(queued.stdout).toContain('Reconcile of alpha queued');
    await pl(['binding', 'reconcile', 'alpha'], { fetch });
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/binding/reconcile`);
  });
});

describe('config', () => {
  test('set takes -f or stdin, and needs one of them on a terminal', async () => {
    const { fetch, requests } = api({});
    await pl(['config', 'set', 'alpha', '--json'], { fetch, stdin: 'a: 1\n' });
    expect(requests.at(-1)!.body).toEqual({ yaml: 'a: 1\n' });
    const dir = mkdtempSync(join(tmpdir(), 'pl-config-'));
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'app.yml'), 'b: 2\n');
    await pl(['config', 'set', 'alpha', '-f', join(dir, 'app.yml'), '--json'], { fetch });
    expect(requests.at(-1)!.body).toEqual({ yaml: 'b: 2\n' });
    const none = await pl(['config', 'set', 'alpha'], { fetch, stdinIsTerminal: true });
    expect(none.stderr).toContain('-f <file>, or pipe it on stdin');
  });

  test('rollback takes N or #N and refuses anything else', async () => {
    const { fetch, requests } = api({});
    await pl(['config', 'rollback', 'alpha', '#3', '--json'], { fetch });
    expect(requests.at(-1)!.body).toEqual({ snapshot: 3 });
    expect(
      (await pl(['config', 'rollback', 'alpha', 'three'], { fetch })).stderr,
    ).toContain("'three' is not a snapshot number.");
  });

  test('check exits 1 when the version could not activate', async () => {
    const { fetch, requests } = api({
      version: 4,
      satisfied: false,
      problems: ['x'],
      missingSecrets: [],
      changedDefaults: [],
      effective: null,
    });
    const result = await pl(['config', 'check', 'alpha', '--version', '4', '--json'], {
      fetch,
    });
    expect(requests.at(-1)!.path).toBe(`/api/pipelines/${ID}/config/check?version=4`);
    expect(result.code).toBe(1);
    expect(result.stderr).toContain("Version 4 can't activate with this config.");
  });
});

/** Stands in for kubectl, helm and tar: records each call, answers by `reply`. */
function fakeCluster(
  reply: (spec: ProcessSpec) => Partial<ProcessResult> | undefined = () => undefined,
) {
  const calls: ProcessSpec[] = [];
  const run = async (spec: ProcessSpec): Promise<ProcessResult> => {
    calls.push(spec);
    return { exitCode: 0, stdout: '', stderr: '', ...(reply(spec) ?? {}) };
  };
  const lines = () => calls.map((c) => [c.command, ...c.args].join(' '));
  return { run, calls, lines };
}

function chart() {
  const dir = mkdtempSync(join(tmpdir(), 'pl-chart-test-'));
  writeFileSync(join(dir, 'Chart.yaml'), 'name: olve-pipelines\n');
  return dir;
}

describe('bootstrap and teardown', () => {
  test('bootstrap refuses prod without --allow-prod and needs a namespace', async () => {
    const cluster = fakeCluster();
    expect(
      (await pl(['bootstrap', '-n', 'apps'], { run: cluster.run })).stderr,
    ).toContain('without --allow-prod');
    expect((await pl(['bootstrap'], { run: cluster.run })).stderr).toContain(
      "'--namespace' (-n) is required.",
    );
    expect(cluster.calls).toHaveLength(0);
  });

  test('bootstrap creates the namespace and credentials, installs the chart and waits for both rollouts', async () => {
    const cluster = fakeCluster((spec) =>
      spec.args[0] === 'get' &&
      (spec.args[1] === 'namespace' || spec.args[1] === 'secret')
        ? { exitCode: 1, stderr: 'NotFound' }
        : undefined,
    );
    const dir = chart();
    const result = await pl(
      [
        'bootstrap',
        '-n',
        'try',
        '--chart',
        dir,
        '--image-tag',
        'v9',
        '--image-pull-policy',
        'Never',
      ],
      {
        run: cluster.run,
      },
    );
    expect(result.code).toBe(0);
    const lines = cluster.lines();
    expect(lines).toContain('kubectl create namespace try');
    const secret = lines.find((l) =>
      l.startsWith('kubectl create secret generic olve-pipelines-minio -n try'),
    )!;
    expect(secret).toMatch(
      /--from-literal=root-user=olve-pipelines --from-literal=root-password=[0-9a-f]{48} --from-literal=rpc-secret=[0-9a-f]{64}$/,
    );
    const helm = lines.find((l) => l.startsWith('helm upgrade --install'))!;
    expect(helm).toContain(
      `olve-pipelines ${dir} -n try -f ${join(dir, 'values-minimal.yaml')}`,
    );
    expect(helm).toContain(
      '--set config.Storage__Endpoint=http://olve-pipelines-garage.try:3900',
    );
    expect(helm).toContain('--set image.tag=v9 --set image.pullPolicy=Never');
    expect(helm).not.toContain('image.repository');
    expect(lines.slice(-2)).toEqual([
      'kubectl -n try rollout status deploy/olve-pipelines-garage --timeout=120s',
      'kubectl -n try rollout status deploy/olve-pipelines --timeout=180s',
    ]);
    expect(result.stdout).toContain(
      "==> Done. Controller 'olve-pipelines' is ready in namespace 'try'.",
    );
  });

  test('a rerun leaves the credentials alone, or adds only a missing rpc-secret', async () => {
    const complete = fakeCluster((spec) =>
      spec.args.at(-1) === 'jsonpath={.data.rpc-secret}' ? { stdout: 'YWJj' } : undefined,
    );
    await pl(['bootstrap', '-n', 'try', '--chart', chart()], { run: complete.run });
    expect(
      complete
        .lines()
        .some((l) => l.includes('create secret') || l.includes('patch secret')),
    ).toBe(false);
    const old = fakeCluster();
    await pl(['bootstrap', '-n', 'try', '--chart', chart()], { run: old.run });
    const patch = old
      .lines()
      .find((l) => l.startsWith('kubectl patch secret olve-pipelines-minio'))!;
    expect(patch).toMatch(/"rpc-secret":"[0-9a-f]{64}"/);
  });

  test('a failing step stops bootstrap without printing the generated credentials', async () => {
    const cluster = fakeCluster((spec) =>
      spec.args[0] === 'get' && spec.args[1] === 'secret'
        ? { exitCode: 1 }
        : spec.args[0] === 'create' && spec.args[1] === 'secret'
          ? { exitCode: 1, stderr: 'forbidden' }
          : undefined,
    );
    const result = await pl(['bootstrap', '-n', 'try', '--chart', chart()], {
      run: cluster.run,
    });
    expect(result.code).toBe(1);
    expect(result.stderr).toContain("'kubectl create secret' failed (exit 1): forbidden");
    expect(result.stderr).not.toContain('root-password');
    expect(cluster.lines().some((l) => l.startsWith('helm upgrade'))).toBe(false);
  });

  test('an unreachable cluster fails the preflight', async () => {
    const cluster = fakeCluster((spec) =>
      spec.args[0] === 'cluster-info'
        ? { exitCode: 1, stderr: 'connection refused' }
        : undefined,
    );
    const result = await pl(['teardown', '-n', 'try'], { run: cluster.run });
    expect(result.stderr).toContain('Cluster is not reachable: connection refused');
  });

  test('teardown uninstalls, deletes the credentials, and purges the data volumes only with --purge-data', async () => {
    const cluster = fakeCluster();
    expect((await pl(['teardown', '-n', 'try'], { run: cluster.run })).code).toBe(0);
    expect(cluster.lines().slice(-2)).toEqual([
      'helm uninstall olve-pipelines -n try',
      'kubectl delete secret olve-pipelines-minio -n try --ignore-not-found',
    ]);
    const gone = fakeCluster((spec) =>
      spec.args[0] === 'status' ? { exitCode: 1 } : undefined,
    );
    const purged = await pl(['teardown', '-n', 'try', '--purge-data'], { run: gone.run });
    expect(purged.stdout).toContain('already removed');
    expect(gone.lines().some((l) => l.startsWith('helm uninstall'))).toBe(false);
    expect(gone.lines().at(-1)).toBe(
      'kubectl delete pvc olve-pipelines-garage-meta olve-pipelines-garage-data -n try --ignore-not-found',
    );
  });
});
