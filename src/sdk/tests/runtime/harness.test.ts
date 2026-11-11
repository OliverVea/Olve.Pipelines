// The harness modes, in-process (same code the compiled binary runs), against the example.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterAll, beforeAll, describe, expect, test } from 'bun:test';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { devConfig } from '../../src/dev/cli.ts';
import { runHarness, type HarnessConfig } from '../../src/harness/main.ts';
import { pipelineData } from '../../src/builder/pipeline.ts';
import { paths, runInfoFromEnv } from '../../src/harness/env.ts';
import { EXIT } from '../../src/harness/exit.ts';
import {
  RESULT_BEGIN,
  RESULT_END,
  MANIFEST_BEGIN,
  MANIFEST_END,
  uploadFile,
} from '../../src/harness/result.ts';
import { parseStrict, strippedKeys } from '../../src/harness/validate.ts';
import {
  callback,
  config,
  external,
  fileRef,
  implement,
  model,
  pipeline,
  step,
  z,
} from '../../src/index.ts';
import { EXAMPLE, SHA, captureIO, tmp, withEnv, removeTmpDirs } from './helpers.ts';

afterAll(removeTmpDirs);

let cfg: HarnessConfig;
beforeAll(async () => {
  cfg = await devConfig(EXAMPLE, true);
});
const bare = () =>
  pipeline({ name: 't', input: model('In', z.object({ x: z.string() })) });
const custom = (
  name: string,
  def: Record<string, unknown>,
  fn: (i: any, ctx: any) => unknown,
) =>
  implement(step({ name, description: '', image: 'alpine', ...def } as any), fn as never);
const ownRef = (step: string) => ({
  run: 1,
  step,
  attempt: 1,
  path: 'missing.txt',
  sha256: 'a'.repeat(64),
});
const AUTH = config('auth', 'Auth.', z.object({ user: z.string() }));
const customSteps = [
  custom(
    'run-info',
    {
      output: model(
        'RunOut',
        z.object({
          pipeline: z.string(),
          id: z.string(),
          number: z.number(),
          attempt: z.number(),
          step: z.string(),
          version: z.string(),
        }),
      ),
    },
    (_, ctx) => ctx.run,
  ),
  custom('callback', {}, (_, ctx) => ctx.skip(ctx.inputUrl('s', 'verdict'))),
  custom('no-output-null', {}, () => null),
  custom('no-output-value', {}, () => ({ x: 1 })),
  custom('out-bad', { output: model('Out', z.object({ x: z.string() })) }, () => ({
    x: 1,
  })),
  custom('cfg', { config: [AUTH] }, () => undefined),
  ...(['refs-missing', 'refs-wrong-sha', 'refs-elsewhere'] as const).map((name) =>
    custom(
      name,
      { output: model('Refs', z.object({ list: z.array(fileRef()) })) },
      async (_, ctx) => {
        if (name === 'refs-missing') return { list: [ownRef(name)] };
        if (name === 'refs-wrong-sha')
          return {
            list: [{ ...(await ctx.output.write('a.txt', 'x')), sha256: 'b'.repeat(64) }],
          };
        const own = ownRef(name);
        return {
          list: [
            { ...own, step: 'other' },
            { ...own, attempt: 2 },
            { ...own, run: 9 },
          ],
        };
      },
    ),
  ),
  ...(['SIGINT', 'SIGTERM'] as const).map((sig) =>
    custom(`cancel-${sig.toLowerCase()}`, {}, () => {
      process.emit(sig as any);
      throw new Error('stopped');
    }),
  ),
  custom('cancel-twice', {}, () => {
    process.emit('SIGINT' as any);
    process.emit('SIGINT' as any);
    return null;
  }),
];
const customPipeline = customSteps.reduce((p: any, s) => p.stage(s), bare());
const servers: { stop(force?: boolean): void }[] = [];
afterAll(() => servers.forEach((s) => s.stop(true)));

const run = (
  args: readonly string[],
  input: unknown,
  env: Record<string, string | undefined> = {},
  dir = tmp('harness'),
) => runIn(cfg, args, input, env, dir);
const runWith = (
  p: unknown,
  args: readonly string[],
  input: unknown,
  env: Record<string, string | undefined> = {},
) => runIn({ ...cfg, pipeline: p }, args, input, env);

async function runIn(
  c: HarnessConfig,
  args: readonly string[],
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
    runHarness(c, args, io),
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
    const one = step({ name: 'one', description: '', image: 'alpine' });
    const two = step({ name: 'two', description: '', image: 'alpine' });
    const bad = { ...cfg, pipeline: bare().stage(one).stage(two) };
    const io = captureIO();
    expect(await runHarness(bad, ['describe'], io)).toBe(EXIT.configInvalid);
    expect(io.err.join('\n')).toMatch(
      /step 'one' has no implementation[\s\S]*step 'two' has no implementation/,
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
  test('exit codes: input 65, output 66, handler 1', async () => {
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
  });
  const USAGE =
    'usage: step describe [--sentinels] | step run <step> | step preflight <step> | step map-trigger <trigger>';
  test.each([
    { args: ['run'], err: USAGE },
    { args: ['map-trigger'], err: USAGE },
    { args: ['bogus'], err: USAGE },
    { args: ['run', 'nope'], err: "unknown step 'nope'" },
    {
      args: ['map-trigger', 'nope'],
      err: "unknown trigger 'nope' (known: push, poll, release)",
    },
    {
      args: ['map-trigger', 'nope'],
      err: "unknown trigger 'nope' (known: none)",
      bare: true,
    },
  ])('usage 64: $args → $err', async ({ args, err, bare: useBare }) => {
    const r = useBare
      ? await runWith(bare(), args, undefined)
      : await run(args, undefined);
    expect(r.code).toBe(EXIT.usage);
    expect(r.io.err).toEqual([err]);
  });
  const mismatch = (step: string, ...issues: string[]) =>
    `step '${step}': input does not match the step's inputs:\n  ${issues.join('\n  ')}`;
  const notObject = "input.json must be a JSON object keyed by the step's input names";
  test.each(
    [
      {
        step: 'checkout',
        input: null,
        code: 65,
        kind: 'input',
        message: mismatch('checkout', notObject),
      },
      {
        step: 'checkout',
        input: [],
        code: 65,
        kind: 'input',
        message: mismatch('checkout', notObject),
      },
      {
        step: 'checkout',
        input: JSON.stringify('x'),
        code: 65,
        kind: 'input',
        message: mismatch('checkout', notObject),
      },
      {
        step: 'checkout',
        input: {},
        code: 65,
        kind: 'input',
        message: mismatch('checkout', 'input.repo: missing', 'input.sha: missing'),
      },
      // found inside when blocks (one and two levels deep)
      {
        step: 'deploy-prod',
        input: {},
        code: 65,
        kind: 'input',
        message: mismatch('deploy-prod', 'input.artifact: missing'),
      },
      {
        step: 'publish-cli',
        input: {},
        code: 65,
        kind: 'input',
        message: mismatch(
          'publish-cli',
          'input.artifact: missing',
          'input.prod: missing',
        ),
      },
      {
        step: 'deploy-prod',
        input: { artifact: { version: '1', commit: { repo: 'r', sha: 1 } } },
        code: 65,
        kind: 'input',
        message: mismatch(
          'deploy-prod',
          'input.artifact.commit.sha: Invalid input: expected string, received number',
          'input.artifact.image: Invalid input: expected object, received undefined',
          'input.artifact.chart: Invalid input: expected object, received undefined',
        ),
      },
      {
        // the old flat shape: handlers get exactly the FailureContext model
        step: 'aoe-triage',
        input: {
          failure: {
            pipeline: 'p',
            run: 1,
            step: 'deploy-prod',
            attempt: 1,
            reason: 'boom',
            logUrl: 'u',
          },
        },
        code: 65,
        kind: 'input',
        message: mismatch(
          'aoe-triage',
          'input.failure.pipeline: Invalid input: expected object, received string',
          'input.failure.run: Invalid input: expected object, received number',
          'input.failure.version: Invalid input: expected object, received undefined',
          'input.failure.input: Invalid input: expected nonoptional, received undefined',
          'input.failure.failedSteps: Invalid input: expected array, received undefined',
        ),
      },
      {
        custom: true,
        step: 'cfg',
        input: { $config: [] },
        code: 65,
        kind: 'input',
        message: mismatch(
          'cfg',
          'input.$config must be an object keyed by config entry names',
        ),
      },
      {
        custom: true,
        step: 'cfg',
        input: {},
        code: 65,
        kind: 'input',
        message: mismatch(
          'cfg',
          "config.auth: Invalid input: expected object, received undefined (config entry 'auth' is not set)",
        ),
      },
      {
        custom: true,
        step: 'cfg',
        input: { $config: { auth: 1 } },
        code: 65,
        kind: 'input',
        message: mismatch(
          'cfg',
          'config.auth: Invalid input: expected object, received number',
        ),
      },
      {
        custom: true,
        step: 'no-output-value',
        input: {},
        code: 66,
        kind: 'output',
        message:
          "step 'no-output-value': returned value does not match its (absent) output model:\n  step 'no-output-value' declares no output model but the handler returned a value",
      },
      {
        custom: true,
        step: 'out-bad',
        input: {},
        code: 66,
        kind: 'output',
        message:
          "step 'out-bad': returned value does not match Out:\n  output.x: Invalid input: expected string, received number",
      },
    ].map((r) => ({
      ...r,
      name: typeof r.input === 'string' ? r.input : JSON.stringify(r.input),
    })),
  )(
    'refusal $code: $step $name',
    async ({ custom: c, step, input, code, kind, message }) => {
      const r = c
        ? await runWith(customPipeline, ['run', step], input)
        : await run(['run', step], input);
      expect(r.code).toBe(code);
      expect(r.envelope).toEqual({ status: 'failed', error: { kind, message } });
    },
  );
  test('an unreadable input.json is refused with the read error', async () => {
    const dir = tmp('noinput');
    const r = await run(['run', 'checkout'], undefined, {}, dir);
    expect(r.code).toBe(EXIT.inputInvalid);
    const message = `cannot read ${dir}/input.json: ENOENT: no such file or directory, open '${dir}/input.json'`;
    expect(r.envelope).toEqual({ status: 'failed', error: { kind: 'input', message } });
    expect(r.io.err).toEqual([message]);
  });
  test('a step without an output model may return null', async () => {
    const r = await runWith(customPipeline, ['run', 'no-output-null'], {});
    expect(r.code).toBe(0);
    expect(r.envelope).toEqual({ status: 'succeeded', output: null });
  });
  test.each([
    {
      env: {
        OLVE_PIPELINE: 'P',
        OLVE_RUN_ID: 'R',
        OLVE_VERSION_STAMP: 'V',
        OLVE_RUN: '3',
        OLVE_ATTEMPT: '2',
      },
      info: { pipeline: 'P', id: 'R', version: 'V', number: 3, attempt: 2 },
    },
    {
      env: {
        OLVE_PIPELINE: undefined,
        OLVE_RUN_ID: undefined,
        OLVE_VERSION_STAMP: undefined,
        OLVE_RUN: undefined,
        OLVE_ATTEMPT: undefined,
      },
      info: {
        pipeline: 'local',
        id: 'local',
        version: '0.0.0-local',
        number: 0,
        attempt: 1,
      },
    },
  ])('ctx.run comes from the environment: $info.pipeline', async ({ env, info }) => {
    const r = await runWith(customPipeline, ['run', 'run-info'], {}, env);
    expect(r.envelope).toEqual({
      status: 'succeeded',
      output: { ...info, step: 'run-info' },
    });
  });
  test.each([
    {
      name: '-1',
      env: { OLVE_RUN: '-1' },
      error: "OLVE_RUN must be a non-negative integer, got '-1'",
    },
    {
      name: '1.5',
      env: { OLVE_RUN: '1.5' },
      error: "OLVE_RUN must be a non-negative integer, got '1.5'",
    },
    {
      name: 'x',
      env: { OLVE_ATTEMPT: 'x' },
      error: "OLVE_ATTEMPT must be a non-negative integer, got 'x'",
    },
  ])(
    'OLVE_RUN / OLVE_ATTEMPT must be non-negative integers: $name',
    async ({ env, error }) => {
      await expect(
        withEnv({ OLVE_RUN: undefined, OLVE_ATTEMPT: undefined, ...env }, () =>
          runInfoFromEnv('s'),
        ),
      ).rejects.toThrow(error);
    },
  );
  test('run 0 is a valid run number; OLVE_DIR defaults to /olve', async () => {
    expect(
      (
        await withEnv({ OLVE_RUN: '0', OLVE_ATTEMPT: undefined }, () =>
          runInfoFromEnv('s'),
        )
      ).number,
    ).toBe(0);
    expect(await withEnv({ OLVE_DIR: undefined }, () => paths().input)).toBe(
      '/olve/input.json',
    );
    expect(await withEnv({ OLVE_DIR: '' }, () => paths().input)).toBe('/olve/input.json');
  });
  const refsFailed = (step: string, path: string, problem: string) => ({
    status: 'failed',
    error: {
      kind: 'output',
      message: `step '${step}': returned value does not match Refs:\n  output.list[0]: file reference '${path}' ${problem}`,
    },
  });
  test.each([
    {
      step: 'refs-missing',
      code: 66,
      envelope: refsFailed(
        'refs-missing',
        'missing.txt',
        'points to this attempt but no such output file was produced',
      ),
    },
    {
      step: 'refs-wrong-sha',
      code: 66,
      envelope: refsFailed(
        'refs-wrong-sha',
        'a.txt',
        'has the wrong sha256 (the file changed after it was referenced)',
      ),
    },
  ])(
    'own file refs are checked at any depth: $step',
    async ({ step, code, envelope }) => {
      const r = await runWith(
        customPipeline,
        ['run', step],
        {},
        { OLVE_RUN: '1', OLVE_ATTEMPT: '1' },
      );
      expect(r.code).toBe(code);
      expect(r.envelope).toEqual(envelope);
    },
  );
  test("refs to another run, step or attempt aren't checked", async () => {
    const r = await runWith(
      customPipeline,
      ['run', 'refs-elsewhere'],
      {},
      { OLVE_RUN: '1', OLVE_ATTEMPT: '1' },
    );
    expect(r.code).toBe(0);
    expect(r.envelope.output.list).toHaveLength(3);
  });
  const noCallback =
    "step 'callback' failed: no callback URL for input 'verdict' of step 's': it must list callback() in from, and the instance needs a public base URL";
  test.each(
    [
      {
        urls: '{"s/verdict":"https://hooks/x"}',
        envelope: { status: 'skipped', reason: 'https://hooks/x' },
      },
      {
        urls: '[',
        envelope: { status: 'failed', error: { kind: 'handler', message: noCallback } },
      },
      {
        urls: undefined,
        envelope: { status: 'failed', error: { kind: 'handler', message: noCallback } },
      },
    ].map((r) => ({ ...r, name: r.urls ?? 'unset' })),
  )('OLVE_CALLBACK_URLS feeds ctx.inputUrl: $name', async ({ urls, envelope }) => {
    const r = await runWith(
      customPipeline,
      ['run', 'callback'],
      {},
      { OLVE_CALLBACK_URLS: urls },
    );
    expect(r.envelope).toEqual(envelope);
  });
  test.each([
    { sig: 'SIGINT', code: EXIT.sigint },
    { sig: 'SIGTERM', code: EXIT.sigterm },
  ])('$sig cancels the handler → exit $code', async ({ sig, code }) => {
    const before = [process.listenerCount('SIGINT'), process.listenerCount('SIGTERM')];
    const step = `cancel-${sig.toLowerCase()}`;
    const r = await runWith(customPipeline, ['run', step], {});
    expect(r.code).toBe(code);
    const message = `step '${step}' was cancelled (${sig}): stopped`;
    expect(r.envelope).toEqual({ status: 'failed', error: { kind: 'handler', message } });
    expect(r.io.err).toEqual([message]);
    expect([process.listenerCount('SIGINT'), process.listenerCount('SIGTERM')]).toEqual(
      before,
    );
  });
  test('a second SIGINT exits at once with 130', async () => {
    const exit = process.exit;
    const exits: unknown[] = [];
    process.exit = ((c?: number) => void exits.push(c)) as typeof process.exit;
    try {
      await runWith(customPipeline, ['run', 'cancel-twice'], {});
    } finally {
      process.exit = exit;
    }
    expect(exits).toEqual([EXIT.sigint]);
  });
  test('a step without an implementation exits 78 and says so', async () => {
    const one = step({ name: 'one', description: '', image: 'alpine' });
    const unimplemented = { ...cfg, pipeline: bare().stage(one) };
    const io = captureIO();
    const dir = tmp('cfg');
    writeFileSync(join(dir, 'input.json'), '{}');
    expect(
      await withEnv({ OLVE_DIR: dir }, () =>
        runHarness(unimplemented, ['run', 'one'], io),
      ),
    ).toBe(EXIT.configInvalid);
    expect(io.err.join('\n')).toContain(
      "step 'one' has no implementation (stage implement(step({...}), fn))",
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
  });
});

describe('preflight', () => {
  const Review = model('PfReview', z.object({ url: z.string() }));
  const Verdict = model('PfVerdict', z.object({ accepted: z.boolean() }));
  const reviewStep = step({
    name: 'review',
    description: '',
    image: 'alpine',
    secrets: [],
    input: {
      review: Review,
      verdict: external(Verdict, { title: 'Accepted?', from: [callback()] }),
    },
  });
  const ran: unknown[] = [];
  const reviewed = implement(
    reviewStep,
    ({ verdict }) => {
      if (!verdict.accepted) throw new Error('rejected');
    },
    {
      preflight: async (input, ctx) => {
        ran.push(input);
        if (input.review.url === 'boom') throw new Error('the tool is down');
        ctx.link(input.review.url, 'Review');
        ctx.log('submitted', { callback: ctx.inputUrl('review', 'verdict') });
      },
    },
  );
  const plain = custom('plain', {}, () => null);
  const p = [reviewed, plain].reduce((acc: any, s) => acc.stage(s), bare());
  const callbacks = {
    OLVE_CALLBACK_URLS: JSON.stringify({ 'review/verdict': 'https://cb/x' }),
  };

  test('runs on the regular inputs, ignores an answer already given, and reports its links', async () => {
    ran.length = 0;
    const r = await runWith(
      p,
      ['preflight', 'review'],
      { review: { url: 'https://vr/r/1' }, verdict: { accepted: true } },
      callbacks,
    );
    expect(r.code).toBe(EXIT.ok);
    expect(ran).toEqual([{ review: { url: 'https://vr/r/1' } }]);
    expect(r.envelope).toEqual({
      status: 'succeeded',
      output: null,
      links: [{ url: 'https://vr/r/1', label: 'Review' }],
    });
    expect(r.io.err).toContain('submitted callback=https://cb/x');
    expect(r.io.out).toContain(RESULT_BEGIN);
  });

  test.each([
    {
      name: 'a throw fails it (exit 1)',
      input: { review: { url: 'boom' } },
      code: EXIT.handlerFailed,
      error: {
        kind: 'handler',
        message: "step 'review' preflight failed: the tool is down",
      },
    },
    {
      name: 'a missing regular input fails it (exit 65)',
      input: { verdict: { accepted: true } },
      code: EXIT.inputInvalid,
      error: {
        kind: 'input',
        message:
          "step 'review' preflight: input does not match the step's inputs:\n  input.review: missing",
      },
    },
  ])('$name', async ({ input, code, error }) => {
    const r = await runWith(p, ['preflight', 'review'], input, callbacks);
    expect(r.code).toBe(code);
    expect(r.envelope).toEqual({ status: 'failed', error });
  });

  test('a step without a preflight exits 78 and says so', async () => {
    const r = await runWith(p, ['preflight', 'plain'], {});
    expect(r.code).toBe(EXIT.configInvalid);
    expect(r.io.err).toEqual([
      "step 'plain' has no preflight (implement(step, fn, { preflight }))",
    ]);
  });

  test('implement refuses a preflight on a step without an external input', () => {
    const noWait = step({
      name: 'no-wait',
      description: '',
      image: 'alpine',
      input: { review: Review },
    });
    expect(() => implement(noWait, () => {}, { preflight: () => {} } as never)).toThrow(
      "step 'no-wait': a preflight needs a step with an external(...) input",
    );
  });
});

describe('result upload (OLVE_RESULT_URL)', () => {
  function server(statuses: readonly number[]) {
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
  test.each([{ status: 200 }, { status: 403 }])(
    'a failed result is uploaded too and keeps its own exit code (upload $status)',
    async ({ status }) => {
      const srv = server([status]);
      const r = await run(['run', 'checkout'], { bad: 1 }, { OLVE_RESULT_URL: srv.url });
      expect(r.code).toBe(EXIT.inputInvalid);
      expect(r.envelope.error.kind).toBe('input');
      expect(srv.bodies).toHaveLength(1);
    },
  );
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
  test('map-trigger uploads its result: output.json only', async () => {
    const srv = server([200]);
    const r = await run(
      ['map-trigger', 'poll'],
      { value: SHA },
      { OLVE_RESULT_URL: srv.url },
    );
    expect(r.code).toBe(0);
    const files = await new Bun.Archive(
      await Bun.file(join(dirFor(srv.port), 'result.tar.gz')).bytes(),
    ).files();
    expect([...files.keys()]).toEqual(['output.json']);
    expect(JSON.parse(await files.get('output.json')!.text())).toEqual(r.envelope);
  });
  const recorder = () => {
    const logs: string[] = [];
    const sleeps: number[] = [];
    return {
      logs,
      sleeps,
      log: (l: string) => void logs.push(l),
      sleep: async (ms: number) => void sleeps.push(ms),
    };
  };
  test.each([
    {
      statuses: [503, 429, 200],
      attempts: undefined,
      sleeps: [1000, 2000],
      failures: [
        'HTTP 503 Service Unavailable; retry 1/4',
        'HTTP 429 Too Many Requests; retry 2/4',
      ],
      error: undefined,
    },
    {
      statuses: [500, 408, 200],
      attempts: undefined,
      sleeps: [1000, 2000],
      failures: [
        'HTTP 500 Internal Server Error; retry 1/4',
        'HTTP 408 Request Timeout; retry 2/4',
      ],
      error: undefined,
    },
    {
      statuses: [503],
      attempts: 2,
      sleeps: [1000],
      failures: ['HTTP 503 Service Unavailable; retry 1/1'],
      error: 'HTTP 503 Service Unavailable',
    },
  ])(
    'transient statuses $statuses are retried with backoff',
    async ({ statuses, attempts, sleeps, failures, error }) => {
      const srv = server(statuses);
      const rec = recorder();
      const redacted = `upload to http://localhost:${srv.port}/runs/p/1/checkout/1/result.tar.gz?…`;
      const upload = uploadFile(srv.url, join(EXAMPLE, 'models.ts'), rec.log, {
        attempts,
        sleep: rec.sleep,
      });
      if (error) await expect(upload).rejects.toThrow(`${redacted} failed: ${error}`);
      else await upload;
      expect(srv.bodies).toHaveLength(sleeps.length + 1);
      expect(rec.sleeps).toEqual([...sleeps]);
      expect(rec.logs).toEqual(
        failures.map((f) => {
          const [detail, retry] = f.split('; ');
          return `${redacted} failed (${detail}); ${retry}`;
        }),
      );
    },
  );
  test.each([
    { attempts: 2, sleeps: [1000] },
    { attempts: 3, sleeps: [1000, 2000] },
  ])(
    'network errors are retried then reported ($attempts attempts)',
    async ({ attempts, sleeps }) => {
      const rec = recorder();
      const error: Error = await uploadFile(
        'http://127.0.0.1:1/x?sig=SECRET',
        join(EXAMPLE, 'models.ts'),
        rec.log,
        { attempts, sleep: rec.sleep },
      ).then(
        () => new Error('resolved'),
        (e: Error) => e,
      );
      const prefix = 'upload to http://127.0.0.1:1/x?… failed: ';
      expect(error.message.startsWith(prefix)).toBe(true);
      const detail = error.message.slice(prefix.length);
      expect(detail).not.toBe('undefined');
      expect(rec.sleeps).toEqual([...sleeps]);
      expect(rec.logs).toEqual(
        sleeps.map(
          (_, i) =>
            `upload to http://127.0.0.1:1/x?… failed (${detail}); retry ${i + 1}/${attempts - 1}`,
        ),
      );
    },
  );
});

describe('select variants (OLVE_CASES)', () => {
  const TARGET = config(
    'target',
    'Where.',
    z.discriminatedUnion('kind', [
      z.object({ kind: z.literal('a') }),
      z.object({ kind: z.literal('b') }),
    ]),
  );
  const variant = (name: string, v: string) => custom(name, {}, (_, ctx) => ctx.skip(v));
  const selectPipeline = (bare() as any).select(TARGET, {
    a: (p: any) => p.stage(variant('deploy', 'a'), variant('only', 'only')),
    b: (p: any) => p.stage(variant('deploy', 'b')),
  });
  const several =
    "step 'deploy' is in several select cases; OLVE_CASES must name the selected one (got [])";
  test.each(
    [
      { step: 'deploy', cases: '["target.kind=a"]', ran: 'a' },
      { step: 'deploy', cases: '[1,"target.kind=b"]', ran: 'b' },
      { step: 'deploy', cases: '["other.kind=x","target.kind=a"]', ran: 'a' },
      { step: 'only', cases: undefined, ran: 'only' },
      { step: 'deploy', cases: undefined, err: several },
      { step: 'deploy', cases: 'nope', err: several },
      { step: 'deploy', cases: '{}', err: several },
      { step: 'deploy', cases: '[null,1]', err: several },
    ].map((r) => ({ ...r, name: r.cases ?? 'unset' })),
  )('run $step with OLVE_CASES=$name', async ({ step, cases, ran, err }) => {
    const r = await runWith(selectPipeline, ['run', step], {}, { OLVE_CASES: cases });
    if (err) {
      expect(r.code).toBe(EXIT.usage);
      expect(r.io.err).toEqual([err]);
    } else {
      expect(r.code).toBe(0);
      expect(r.envelope).toEqual({ status: 'skipped', reason: ran });
    }
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
  });
  const pollEvent = `poll trigger 'poll': the event must be {"value": "<string>"}`;
  const pushEvent = 'push trigger: the event must be the webhook payload object';
  const webhookEvent = `webhook trigger 'release': the event must be {"headers": {...}, "body": ...}`;
  test.each(
    [
      ...[JSON.stringify('x'), null, { value: 1 }, { other: 1 }].map((event) => ({
        trigger: 'poll',
        event,
        message: pollEvent,
      })),
      ...[JSON.stringify('x'), null, []].map((event) => ({
        trigger: 'push',
        event,
        message: pushEvent,
      })),
      ...[
        JSON.stringify('x'),
        null,
        [],
        { headers: {} },
        { body: {} },
        { body: {}, headers: null },
        { body: {}, headers: [] },
      ].map((event) => ({ trigger: 'release', event, message: webhookEvent })),
    ].map((r) => ({
      ...r,
      name: typeof r.event === 'string' ? r.event : JSON.stringify(r.event),
    })),
  )('$trigger refuses the event $name (65)', async ({ trigger, event, message }) => {
    const r = await run(['map-trigger', trigger], event);
    expect(r.code).toBe(EXIT.inputInvalid);
    expect(r.envelope).toEqual({ status: 'failed', error: { kind: 'input', message } });
    expect(r.io.err).toEqual([message]);
  });
  test('an unreadable event is refused with the read error (65)', async () => {
    const dir = tmp('noevent');
    const r = await run(['map-trigger', 'push'], undefined, {}, dir);
    expect(r.code).toBe(EXIT.inputInvalid);
    expect(r.envelope).toEqual({
      status: 'failed',
      error: {
        kind: 'input',
        message: `cannot read ${dir}/input.json: ENOENT: no such file or directory, open '${dir}/input.json'`,
      },
    });
  });
  const onPoll = (map: (v: string) => unknown) =>
    bare().onPoll(
      { url: 'https://example.com', valuePath: 'v', intervalSeconds: 60 },
      map as never,
    );
  const onPush = (map: (e: unknown) => unknown) => bare().onPush(map as never);
  const fails = (kind: string, message: string) => ({
    status: 'failed',
    error: { kind, message },
  });
  test.each([
    {
      name: 'Error',
      p: onPoll(() => {
        throw new Error('boom');
      }),
      code: 1,
      envelope: fails('handler', "trigger 'poll' mapper threw: boom"),
    },
    {
      name: 'string',
      p: onPoll(() => {
        throw 'str';
      }),
      code: 1,
      envelope: fails('handler', "trigger 'poll' mapper threw: str"),
    },
    {
      name: 'undefined',
      p: onPush(() => {
        throw undefined;
      }),
      code: 1,
      envelope: fails('handler', "trigger 'push' mapper threw: undefined"),
    },
    {
      name: 'poll null',
      p: onPoll(() => null),
      code: 66,
      envelope: fails(
        'output',
        "trigger 'poll': mapped input does not match In:\n  input: Invalid input: expected object, received null",
      ),
    },
    {
      name: 'push null',
      p: onPush(() => null),
      code: 66,
      envelope: fails(
        'output',
        "trigger 'push': mapped input does not match In:\n  input: Invalid input: expected object, received null",
      ),
    },
    {
      name: 'invalid',
      p: onPoll(() => ({})),
      code: 66,
      envelope: fails(
        'output',
        "trigger 'poll': mapped input does not match In:\n  input.x: Invalid input: expected string, received undefined",
      ),
    },
  ])('mapper outcome: $name', async ({ p, code, envelope }) => {
    const trigger = pipelineData(p).triggers[0]!.name;
    const r = await runWith(
      p,
      ['map-trigger', trigger],
      trigger === 'poll' ? { value: 'v' } : {},
    );
    expect(r.code).toBe(code);
    expect(r.envelope).toEqual(envelope);
  });
  test('invalid mapped input → 66; mapper throws → 1', async () => {
    const bad = await run(['map-trigger', 'push'], {
      ref: 'refs/heads/main',
      after: 'nope',
      repository: { full_name: 'o/r' },
    });
    expect(bad.code).toBe(EXIT.outputInvalid);
    expect(bad.envelope).toMatchObject({ status: 'failed', error: { kind: 'output' } });
    const threw = await run(['map-trigger', 'push'], { ref: 'x', after: SHA });
    expect(threw.code).toBe(EXIT.handlerFailed);
  });
});
