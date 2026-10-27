// The harness modes, in-process (same code the compiled binary runs), against the example.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterAll, beforeAll, describe, expect, test } from 'bun:test';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { devConfig } from '../../src/dev/cli.ts';
import { runHarness, type HarnessConfig } from '../../src/harness/main.ts';
import { EXIT } from '../../src/harness/exit.ts';
import {
  RESULT_BEGIN,
  RESULT_END,
  MANIFEST_BEGIN,
  MANIFEST_END,
  uploadFile,
} from '../../src/harness/result.ts';
import { parseStrict, strippedKeys } from '../../src/harness/validate.ts';
import { z } from '../../src/index.ts';
import { EXAMPLE, SHA, captureIO, tmp, withEnv } from './helpers.ts';

let cfg: HarnessConfig;
beforeAll(async () => {
  cfg = await devConfig(EXAMPLE, true);
});
const servers: { stop(force?: boolean): void }[] = [];
afterAll(() => servers.forEach((s) => s.stop(true)));

async function run(
  args: string[],
  input: unknown,
  env: Record<string, string | undefined> = {},
  dir = tmp('harness'),
) {
  if (input !== undefined)
    writeFileSync(
      join(dir, 'input.json'),
      typeof input === 'string' ? input : JSON.stringify(input),
    );
  const io = captureIO();
  const code = await withEnv({ OLVE_DIR: dir, OLVE_RESULT_URL: undefined, ...env }, () =>
    runHarness(cfg, args, io),
  );
  const outFile = join(dir, 'work', 'output.json');
  const envelope = existsSync(outFile)
    ? JSON.parse(readFileSync(outFile, 'utf8'))
    : undefined;
  return { code, io, dir, envelope };
}

describe('describe', () => {
  test('prints the canonical manifest; --sentinels wraps it', async () => {
    const a = await run(['describe'], undefined);
    expect(a.code).toBe(0);
    expect(JSON.parse(a.io.out).apiVersion).toBe('2.0');
    const b = await run(['describe', '--sentinels'], undefined);
    expect(b.io.out).toBe(`${MANIFEST_BEGIN}\n${a.io.out}${MANIFEST_END}\n`);
  });
  test('an invalid pipeline exits 78 with all problems on stderr', async () => {
    const bad = { ...cfg, handlers: {} };
    const io = captureIO();
    expect(await runHarness(bad, ['describe'], io)).toBe(EXIT.configInvalid);
    expect(io.err.join('\n')).toMatch(
      /step 'checkout' has no handler[\s\S]*step 'build' has no handler/,
    );
  });
});

describe('run', () => {
  test('success: envelope in work/output.json, sentinel block on stdout', async () => {
    const r = await run(
      ['run', 'checkout'],
      { repo: 'OliverVea/Olve.Pipelines', sha: SHA },
      { OLVE_RUN: '5', OLVE_ATTEMPT: '2', GITHUB_TOKEN: 't' },
    );
    expect(r.code).toBe(0);
    expect(r.envelope).toMatchObject({
      status: 'succeeded',
      output: { sha: SHA, tree: { run: 5, attempt: 2, step: 'checkout', path: 'tree/' } },
    });
    const block = r.io.out.split('\n');
    expect(block[0]).toBe(RESULT_BEGIN);
    expect(JSON.parse(block[1]!)).toEqual(r.envelope);
    expect(block[2]).toBe(RESULT_END);
    expect(existsSync(join(r.dir, 'work/files/tree.tar'))).toBe(true);
  });
  test('skipped envelope (exit 0)', async () => {
    const co = await run(['run', 'checkout'], { repo: 'r', sha: SHA });
    const build = await run(
      ['run', 'build'],
      { source: co.envelope.output },
      { OLVE_FILES_DIR: undefined },
    );
    expect(build.code).toBe(EXIT.handlerFailed); // build fetches the tree, which has no source here
    const r = await run(
      ['run', 'deploy-beta'],
      {
        artifact: {
          version: '1',
          commit: { repo: 'r', sha: SHA },
          image: co.envelope.output.tree,
          chart: co.envelope.output.tree,
        },
      },
      { EXAMPLE_SKIP_BETA: '1' },
    );
    expect(r.code).toBe(0);
    expect(r.envelope).toEqual({ status: 'skipped', reason: 'EXAMPLE_SKIP_BETA is set' });
  });
  test('exit codes: usage 64, input 65, output 66, handler 1, config 78', async () => {
    expect((await run(['run'], undefined)).code).toBe(EXIT.usage);
    expect((await run(['run', 'nope'], undefined)).code).toBe(EXIT.usage);
    expect((await run(['bogus'], undefined)).code).toBe(EXIT.usage);

    const missing = await run(['run', 'checkout'], undefined);
    expect(missing.code).toBe(EXIT.inputInvalid);
    expect(missing.envelope).toMatchObject({
      status: 'failed',
      error: { kind: 'input' },
    });
    expect((await run(['run', 'checkout'], '{not json')).code).toBe(EXIT.inputInvalid);
    const invalid = await run(['run', 'checkout'], { repo: 1, sha: SHA });
    expect(invalid.code).toBe(EXIT.inputInvalid);
    expect(invalid.envelope.error.message).toMatch(/input.repo/);

    const co = await run(['run', 'checkout'], { repo: 'r', sha: SHA });
    const art = {
      version: '1',
      commit: { repo: 'r', sha: SHA },
      image: co.envelope.output.tree,
      chart: co.envelope.output.tree,
    };
    const out = await run(
      ['run', 'deploy-beta'],
      { artifact: art },
      { EXAMPLE_BETA_URL: 'not a url', SSH_PRIVATE_KEY: 'k', OLVE_FILES_DIR: undefined },
    );
    expect(out.code).toBe(EXIT.handlerFailed); // fetch fails before the bad URL is returned
    const failed = await run(
      ['run', 'deploy-beta'],
      { artifact: art },
      { SSH_PRIVATE_KEY: undefined },
    );
    expect(failed.code).toBe(EXIT.handlerFailed);
    expect(failed.envelope).toMatchObject({
      status: 'failed',
      error: { kind: 'handler' },
    });

    const broken = {
      ...cfg,
      handlers: { ...cfg.handlers, checkout: cfg.handlers.build! },
    };
    const io = captureIO();
    const dir = tmp('cfg');
    writeFileSync(join(dir, 'input.json'), '{}');
    expect(
      await withEnv({ OLVE_DIR: dir }, () => runHarness(broken, ['run', 'checkout'], io)),
    ).toBe(EXIT.configInvalid);
    expect(io.err.join('\n')).toMatch(
      /handler of step 'checkout' implements a different step/,
    );
  });
  test('output validated strictly (unknown keys at any depth → 66)', async () => {
    const summaryOk = await run(['run', 'summary'], {
      beta: { release: 'r', version: 'v', url: 'https://x' },
    });
    expect(summaryOk.envelope).toEqual({ status: 'succeeded', output: null });
    expect(
      strippedKeys(
        { a: 1, b: { c: 1, d: [{ e: 1, f: 2 }] } },
        { a: 1, b: { c: 1, d: [{ e: 1 }] } },
      ),
    ).toEqual(['b.d[0].f']);
    const S = z.object({
      a: z.object({ b: z.string() }),
      r: z.record(z.string(), z.object({ x: z.number() })),
    });
    expect(parseStrict(S, { a: { b: 'x', c: 1 }, r: {} }, 'output')).toEqual({
      ok: false,
      issues: ['output.a.c: unknown field (outputs are validated strictly)'],
    });
    expect(
      parseStrict(S, { a: { b: 'x' }, r: { k: { x: 1, y: 2 } } }, 'output'),
    ).toMatchObject({ ok: false });
    expect(parseStrict(S, { a: { b: 'x' }, r: { k: { x: 1 } } }, 'output')).toMatchObject(
      { ok: true },
    );
  });
  test('stale output.json is removed before running', async () => {
    const dir = tmp('stale');
    mkdirSync(join(dir, 'work'), { recursive: true });
    writeFileSync(
      join(dir, 'work/output.json'),
      '{"status":"succeeded","output":"stale"}',
    );
    const r = await run(['run', 'checkout'], { nope: 1 }, {}, dir);
    expect(r.envelope.status).toBe('failed');
  });
  test('failure handler steps can be run', async () => {
    const failure = {
      pipeline: { id: 'p', name: 'Olve.Pipelines' },
      run: {
        id: 'r',
        number: 1,
        address: 'p#1',
        versionStamp: '2026.10.21.1',
        trigger: 'push',
        url: 'u/runs/1',
      },
      version: { number: 3, commit: null },
      input: { sha: 'abc' },
      reason: "step 'deploy-prod' failed: boom",
      failedSteps: [
        {
          step: 'deploy-prod',
          attempt: 1,
          address: 'p#1/deploy-prod@1',
          reason: 'boom',
          logUrl: 'u/logs',
        },
      ],
    };
    const r = await run(['run', 'aoe-triage'], { failure });
    expect(r.code).toBe(0);
    expect(r.io.err.join('\n')).toMatch(/would open an agent session run=p#1/);
    // The old flat shape is rejected: handlers get exactly the FailureContext model.
    const old = await run(['run', 'aoe-triage'], {
      failure: {
        pipeline: 'p',
        run: 1,
        step: 'deploy-prod',
        attempt: 1,
        reason: 'boom',
        logUrl: 'u',
      },
    });
    expect(old.code).toBe(65);
  });
});

describe('result upload (OLVE_RESULT_URL)', () => {
  function server(statuses: number[]) {
    const bodies: {
      url: string;
      size: number;
      contentLength: string | null;
      te: string | null;
    }[] = [];
    let i = 0;
    const s = Bun.serve({
      port: 0,
      async fetch(req) {
        const body = new Uint8Array(await req.arrayBuffer());
        bodies.push({
          url: req.url,
          size: body.length,
          contentLength: req.headers.get('content-length'),
          te: req.headers.get('transfer-encoding'),
        });
        const status = statuses[Math.min(i++, statuses.length - 1)]!;
        if (status === 200) await Bun.write(join(dirFor(s.port!), 'result.tar.gz'), body);
        return new Response(status === 200 ? '' : 'err', { status });
      },
    });
    servers.push(s);
    return {
      url: `http://localhost:${s.port}/runs/p/1/checkout/1/result.tar.gz?X-Amz-Credential=SECRET&X-Amz-Signature=x`,
      bodies,
      port: s.port!,
    };
  }
  const dirs = new Map<number, string>();
  const dirFor = (port: number) =>
    dirs.get(port) ?? (dirs.set(port, tmp('upload')), dirs.get(port)!);

  test('succeeded result is packed (output.json + files/) and PUT', async () => {
    const srv = server([200]);
    const r = await run(
      ['run', 'checkout'],
      { repo: 'r', sha: SHA },
      { OLVE_RESULT_URL: srv.url },
    );
    expect(r.code).toBe(0);
    expect(srv.bodies).toHaveLength(1);
    // pre-signed S3 PUTs need a Content-Length (no chunked transfer encoding)
    expect(srv.bodies[0]!.contentLength).toBe(String(srv.bodies[0]!.size));
    expect(srv.bodies[0]!.te).toBeNull();
    const files = await new Bun.Archive(
      await Bun.file(join(dirFor(srv.port), 'result.tar.gz')).bytes(),
    ).files();
    expect([...files.keys()].sort()).toEqual(['files/tree.tar', 'output.json']);
    expect(JSON.parse(await files.get('output.json')!.text())).toEqual(r.envelope);
    expect(r.io.err.join('\n')).not.toContain('SECRET');
  });
  test('failed results are uploaded too; transient errors are retried', async () => {
    const srv = server([503, 429, 200]);
    const r = await run(['run', 'checkout'], { bad: 1 }, { OLVE_RESULT_URL: srv.url });
    expect(r.code).toBe(EXIT.inputInvalid);
    expect(srv.bodies).toHaveLength(3);
    expect(r.io.err.join('\n')).toMatch(/retry 1\/4/);
    expect(r.io.err.join('\n')).not.toContain('SECRET');
  }, 15000);
  test('4xx fails fast → exit 74 (envelope still printed)', async () => {
    const srv = server([403]);
    const r = await run(
      ['run', 'checkout'],
      { repo: 'r', sha: SHA },
      { OLVE_RESULT_URL: srv.url },
    );
    expect(r.code).toBe(EXIT.uploadFailed);
    expect(srv.bodies).toHaveLength(1);
    expect(r.io.out).toContain(RESULT_BEGIN);
    expect(r.io.err.join('\n')).toMatch(
      /upload to http:\/\/localhost:\d+\/runs\/p\/1\/checkout\/1\/result.tar.gz\?… failed: HTTP 403/,
    );
  });
  test('network errors are retried then reported', async () => {
    await expect(
      uploadFile(
        'http://127.0.0.1:1/x?sig=SECRET',
        join(EXAMPLE, 'models.ts'),
        () => {},
        { attempts: 2, baseDelayMs: 1 },
      ),
    ).rejects.toThrow(/127.0.0.1:1\/x\?… failed/);
  });
});

describe('map-trigger', () => {
  test('push: mapper runs as code (ternary on values), defaults applied (z.output)', async () => {
    const main = await run(['map-trigger', 'push'], {
      ref: 'refs/heads/main',
      after: SHA,
      repository: { full_name: 'o/r' },
    });
    expect(main.code).toBe(0);
    expect(main.envelope).toEqual({
      status: 'succeeded',
      output: { repo: 'o/r', sha: SHA, deployTo: 'prod', publishCli: true },
    });
    const dev = await run(['map-trigger', 'push'], {
      ref: 'refs/heads/v2',
      after: SHA,
      repository: { full_name: 'o/r' },
    });
    expect(dev.envelope.output.deployTo).toBe('beta');
  });
  test('poll: event is {value}', async () => {
    const r = await run(['map-trigger', 'poll'], { value: SHA });
    expect(r.envelope).toEqual({
      status: 'succeeded',
      output: {
        repo: 'OliverVea/Olve.Pipelines',
        sha: SHA,
        deployTo: 'prod',
        publishCli: true,
      },
    });
    expect((await run(['map-trigger', 'poll'], { other: 1 })).code).toBe(
      EXIT.inputInvalid,
    );
  });
  test('webhook: event is {headers, body}; null ignores the delivery (skipped, exit 0)', async () => {
    const release = await run(['map-trigger', 'release'], {
      headers: { 'x-github-event': 'release' },
      body: { release: { target_commitish: SHA } },
    });
    expect(release.envelope).toEqual({
      status: 'succeeded',
      output: {
        repo: 'OliverVea/Olve.Pipelines',
        sha: SHA,
        deployTo: 'prod',
        publishCli: true,
      },
    });
    const ping = await run(['map-trigger', 'release'], {
      headers: { 'x-github-event': 'ping' },
      body: {},
    });
    expect(ping.code).toBe(0);
    expect(ping.envelope).toEqual({
      status: 'skipped',
      reason: "trigger 'release' ignored the delivery",
    });
    expect((await run(['map-trigger', 'release'], { body: {} })).code).toBe(
      EXIT.inputInvalid,
    );
  });
  test('invalid mapped input → 66; mapper throws → 1; unknown trigger → 64', async () => {
    const bad = await run(['map-trigger', 'push'], {
      ref: 'refs/heads/main',
      after: 'nope',
      repository: { full_name: 'o/r' },
    });
    expect(bad.code).toBe(EXIT.outputInvalid);
    expect(bad.envelope).toMatchObject({ status: 'failed', error: { kind: 'output' } });
    const threw = await run(['map-trigger', 'push'], { ref: 'x', after: SHA });
    expect(threw.code).toBe(EXIT.handlerFailed);
    expect((await run(['map-trigger', 'nope'], {})).code).toBe(EXIT.usage);
  });
});
