import { describe, expect, test } from 'bun:test';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { assemble } from '../src/dev/assemble';
import { parseManifest, type Json, type JsonObject } from '../src/dev/manifest';
import { flatten, placeStep, select, whenMismatch } from '../src/dev/plan';
import { Pool, Workspace } from '../src/dev/pool';
import {
  buildPipelineInput,
  missingRequired,
  parseList,
  parseSecrets,
} from '../src/commands/dev';
import type { ProcessResult, ProcessSpec } from '../src/process';
import { fakeFetch, ID, pl, type Recorded } from './helpers';

const demoText = readFileSync(
  join(import.meta.dir, 'fixtures/demo.manifest.json'),
  'utf8',
);
const selectText = readFileSync(
  join(import.meta.dir, 'fixtures/select.manifest.json'),
  'utf8',
);
const matrixText = readFileSync(
  join(import.meta.dir, 'fixtures/matrix.manifest.json'),
  'utf8',
);
const demo = () => parseManifest(demoText);
const SHA = '0123456789abcdef0123456789abcdef01234567';
const SSH: JsonObject = { target: { kind: 'ssh', host: 'box' } };

type Script = (
  input: JsonObject,
  olveDir: string,
  env: Record<string, string | null | undefined>,
) => { envelope: JsonObject; exitCode: number };

/** Stands in for bun: `describe` prints the manifest, `run` writes the step's envelope where the harness would. */
function fakeHarness(manifest: string) {
  const calls: ProcessSpec[] = [];
  const inputs: Record<string, JsonObject> = {};
  const steps: Record<string, Script> = {};
  const state = { describeExit: 0, describeStderr: '' };
  const run = async (spec: ProcessSpec): Promise<ProcessResult> => {
    calls.push(spec);
    const args = spec.args;
    if (args[0] === '-e') return { exitCode: 0, stdout: '', stderr: '' };
    if (args[1] === 'describe')
      return state.describeExit === 0
        ? { exitCode: 0, stdout: manifest, stderr: '' }
        : { exitCode: state.describeExit, stdout: '', stderr: state.describeStderr };
    if (args[1] === 'run') {
      const step = args[3]!;
      const olveDir = args[5]!;
      const input = JSON.parse(
        readFileSync(join(olveDir, 'input.json'), 'utf8'),
      ) as JsonObject;
      inputs[step] = input;
      const { envelope, exitCode } = steps[step]?.(input, olveDir, spec.env ?? {}) ?? {
        envelope: { status: 'succeeded', output: { from: step } },
        exitCode: 0,
      };
      mkdirSync(join(olveDir, 'work'), { recursive: true });
      writeFileSync(join(olveDir, 'work', 'output.json'), JSON.stringify(envelope));
      spec.onStdoutLine?.(`log from ${step}`);
      spec.onStdoutLine?.('---OLVE-RESULT-BEGIN---');
      spec.onStdoutLine?.(JSON.stringify(envelope));
      spec.onStdoutLine?.('---OLVE-RESULT-END---');
      return { exitCode, stdout: '', stderr: '' };
    }
    return { exitCode: 0, stdout: '', stderr: '' };
  };
  const ran = () => calls.filter((c) => c.args[1] === 'run').map((c) => c.args[3]);
  return { run, calls, inputs, steps, state, ran };
}

/** A pipeline directory with a fake SDK beside it, so the toolchain resolves without bun. */
function pipelineDir() {
  const root = mkdtempSync(join(tmpdir(), 'pl-dev-'));
  const dir = join(root, '.pipelines');
  mkdirSync(dir);
  writeFileSync(join(dir, 'package.json'), '{}\n');
  const sdk = join(root, 'sdk');
  mkdirSync(join(sdk, 'src/dev'), { recursive: true });
  writeFileSync(
    join(sdk, 'package.json'),
    JSON.stringify({
      bin: { 'olve-dev': './src/dev/cli.ts', 'olve-build': './src/build/cli.ts' },
    }),
  );
  writeFileSync(join(sdk, 'src/dev/cli.ts'), '');
  const env = { OLVE_SDK: sdk, OLVE_BUN: process.execPath };
  return { root, dir, env, workspace: new Workspace(join(dir, '.olve')) };
}

async function dev(
  argv: string[],
  harness: ReturnType<typeof fakeHarness>,
  p: ReturnType<typeof pipelineDir>,
  extra: {
    prompt?: string;
    terminal?: boolean;
    env?: Record<string, string>;
    fetch?: typeof fetch;
  } = {},
) {
  const questions: string[] = [];
  const result = await pl(['dev', ...argv, '--dir', p.dir], {
    fetch: extra.fetch,
    run: harness.run,
    env: { ...p.env, ...(extra.env ?? {}) },
    stdinIsTerminal: extra.terminal ?? false,
    prompt: async (q) => {
      questions.push(q);
      return extra.prompt ?? 'n';
    },
  });
  return { ...result, questions };
}

describe('plan', () => {
  test('stages are numbered and nested when conditions accumulate', () => {
    const plan = flatten(demo());
    expect(plan.map((p) => `${p.step.name}:${p.stage}:${p.conditions.length}`)).toEqual([
      'src:1:0',
      'build:2:0',
      'lint:2:0',
      'deploy:3:1',
      'publish:4:2',
      'summary:5:0',
      'notify:6:0',
    ]);
  });

  test('a config keeps only its select case and records it; without one every case runs', () => {
    const m = parseManifest(selectText);
    const plan = flatten(m, SSH);
    expect(plan.map((p) => `${p.step.name}:${p.step.image}`)).toEqual([
      'build:build-img',
      'deploy:ssh-img',
      'summary:build-img',
    ]);
    expect(plan.find((p) => p.step.name === 'deploy')!.cases).toEqual([
      'target.kind=ssh',
    ]);
    expect(flatten(m).filter((p) => p.step.name === 'deploy')).toHaveLength(2);
    expect(() => placeStep(m, 'deploy')).toThrow('several select cases');
    expect(placeStep(m, 'deploy', SSH).step.image).toBe('ssh-img');
    expect(() => placeStep(m, 'nope', SSH)).toThrow("Unknown step 'nope'");
  });

  test('when matches any listed value, every condition must hold', () => {
    const conditions = [
      { field: 'deployTo', values: ['beta', 'prod'] },
      { field: 'publish', values: [true] },
    ];
    expect(whenMismatch(conditions, { deployTo: 'prod', publish: true })).toBeUndefined();
    expect(whenMismatch(conditions, { deployTo: 'prod', publish: false })).toContain(
      'publish = false',
    );
    expect(whenMismatch(conditions, {})).toContain('deployTo = unset');
  });

  test('--until runs through that stage, --only filters, unknown names fail', () => {
    const plan = flatten(demo());
    expect(select(plan, 'build').map((p) => p.step.name)).toEqual([
      'src',
      'build',
      'lint',
    ]);
    expect(select(plan, undefined, ['lint', 'summary']).map((p) => p.step.name)).toEqual([
      'lint',
      'summary',
    ]);
    expect(() => select(plan, 'nope')).toThrow("--until: unknown step 'nope'");
  });
});

describe('input', () => {
  test('fields are typed by the model, defaults fill the rest, earlier values stay', () => {
    const m = demo();
    expect(
      buildPipelineInput(m.inputSchema, undefined, [
        `sha=${SHA}`,
        'replicas=3',
        'publish=true',
      ]),
    ).toEqual({
      sha: SHA,
      replicas: 3,
      publish: true,
      deployTo: 'beta',
    });
    expect(
      buildPipelineInput(m.inputSchema, { sha: 'a', deployTo: 'prod' }, ['replicas=2']),
    ).toEqual({
      sha: 'a',
      deployTo: 'prod',
      replicas: 2,
      publish: false,
    });
    expect(() => buildPipelineInput(m.inputSchema, undefined, ['nope=1'])).toThrow(
      "Unknown pipeline input field 'nope'",
    );
    expect(() => buildPipelineInput(m.inputSchema, undefined, ['replicas=x'])).toThrow(
      'is an integer',
    );
    expect(missingRequired(m.inputOutputSchema, { deployTo: 'beta' })).toEqual([
      'sha',
      'publish',
      'replicas',
    ]);
  });

  test('secrets split on the first =, lists split on commas across repeats', () => {
    expect(parseSecrets(['A=b=c', 'B='])).toEqual({ A: 'b=c', B: '' });
    expect(() => parseSecrets(['=x'])).toThrow('NAME=value');
    expect(parseList(['a,b', ' c '])).toEqual(['a', 'b', 'c']);
    expect(parseList([])).toBeUndefined();
  });

  test('assembly resolves input refs, consts, nested objects and step outputs from the pool', () => {
    const m = demo();
    const pool = new Pool();
    pool.input = { sha: SHA, deployTo: 'beta' };
    const src = m.stages
      .flatMap((n) => (n.kind === 'stage' ? n.steps : []))
      .find((s) => s.name === 'src')!;
    expect(assemble(src, pool).input).toEqual({
      sha: SHA,
      opts: { depth: 1, target: 'beta' },
    });
    pool.steps.set('src', { status: 'succeeded', output: { sha: SHA } });
    const build = flatten(m).find((p) => p.step.name === 'build')!.step;
    expect(assemble(build, pool).input).toEqual({ source: { sha: SHA }, commit: SHA });
  });

  test('a missing or failed root is a problem naming it; a skipped root skips, an optional input is left out', () => {
    const plan = flatten(demo());
    const step = (name: string) => plan.find((p) => p.step.name === name)!.step;
    const pool = new Pool();
    pool.input = {};
    expect(assemble(step('src'), pool).problems[0]).toContain(
      "pipeline input field 'sha' is not set",
    );
    expect(assemble(step('build'), pool).problems[0]).toContain(
      'run `pl dev run src` first',
    );
    pool.steps.set('src', { status: 'failed', reason: 'boom' });
    expect(assemble(step('build'), pool).problems[0]).toBe(
      "input 'source' needs the output of 'src', which failed (boom); rerun `pl dev run src`",
    );
    pool.steps.set('build', { status: 'succeeded', output: {} });
    pool.steps.set('deploy', { status: 'skipped', reason: 'when' });
    const summary = assemble(step('summary'), pool);
    expect(summary.input).toEqual({ artifact: {} });
    expect(summary.skippedBecause).toEqual([]);
    expect(assemble(step('notify'), pool).skippedBecause[0]).toContain(
      "needs 'deploy', which was skipped (when)",
    );
    expect(assemble(demo().failureHandlers[0]!.step, pool).problems[0]).toContain(
      '--input-file',
    );
  });

  test('the pool keeps succeeded outputs with their files, round-trips, and refuses a corrupt file', () => {
    const p = pipelineDir();
    const ws = p.workspace;
    mkdirSync(ws.workFiles, { recursive: true });
    writeFileSync(join(ws.workFiles, 'a.txt'), 'a');
    const pool = new Pool();
    pool.record('src', { status: 'succeeded', output: { x: 1 } }, ws);
    pool.record('lint', { status: 'failed', error: { message: 'boom' } }, ws);
    pool.save(ws.poolJson);
    expect(existsSync(join(ws.poolFilesFor('src'), 'a.txt'))).toBe(true);
    const back = Pool.load(ws.poolJson);
    expect(back.steps.get('src')).toEqual({
      status: 'succeeded',
      output: { x: 1 },
      reason: undefined,
    });
    expect(back.steps.get('lint')?.reason).toBe('boom');
    writeFileSync(ws.poolJson, '{nope');
    expect(() => Pool.load(ws.poolJson)).toThrow('not valid JSON');
    expect(Pool.load(join(p.root, 'missing.json')).steps.size).toBe(0);
  });
});

describe('pl dev', () => {
  test('describe prints a summary, or the raw manifest with --json; a failing describe keeps its exit code', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    const human = await dev(['describe'], h, p);
    expect(human.code).toBe(0);
    expect(human.stdout).toContain('demo — A demo pipeline.');
    expect(human.stdout).toContain('input:    Req { sha*, deployTo="beta" (beta|prod)');
    expect(human.stdout).toContain('when deployTo in ["prod"]:');
    expect(human.stdout).toContain('[waits for: verdict (Verdict)]');
    expect(human.stdout).toContain('triage                 on deploy');
    expect((await dev(['describe', '--json'], h, p)).stdout).toBe(demoText);
    h.state.describeExit = 78;
    h.state.describeStderr = 'step src: image is empty';
    const failed = await dev(['describe'], h, p);
    expect(failed.code).toBe(78);
    expect(failed.stderr).toContain('describe failed (exit 78)');
    expect(failed.stderr).toContain('step src: image is empty');
  });

  test('a missing package.json or SDK says what to do', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    expect(
      (await pl(['dev', 'describe', '--dir', p.root], { run: h.run, env: p.env })).stderr,
    ).toContain('No package.json');
    const noSdk = await pl(['dev', 'describe', '--dir', p.dir], {
      run: h.run,
      env: { OLVE_BUN: process.execPath },
    });
    expect(noSdk.stderr).toContain('run `bun install` there');
  });

  test.each([
    ['succeeded', { status: 'succeeded', output: null }, 0],
    ['skipped', { status: 'skipped', reason: 'nothing to do' }, 0],
    ['failed', { status: 'failed', error: { kind: 'handler', message: 'boom' } }, 1],
  ])(
    'run prints the links of a %s step after its result',
    async (_, envelope, exitCode) => {
      const p = pipelineDir();
      const h = fakeHarness(demoText);
      h.steps.src = () => ({
        envelope: {
          ...envelope,
          links: [
            { url: 'https://beta.example.com', label: 'Beta' },
            { url: 'https://x.example.com' },
          ] as Json[],
        },
        exitCode,
      });
      const r = await dev(['run', 'src', '--field', `sha=${SHA}`], h, p);
      const lines = r.stdout.trimEnd().split('\n');
      expect(lines.slice(-2)).toEqual([
        '  → https://beta.example.com (Beta)',
        '  → https://x.example.com',
      ]);
    },
  );

  test('run mirrors the harness exit code, prints the envelope, and successive runs chain through the pool', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    h.steps.src = (input) => ({
      envelope: { status: 'succeeded', output: { sha: input.sha! } },
      exitCode: 0,
    });
    const src = await dev(['run', 'src', '--field', `sha=${SHA}`], h, p);
    expect(src.code).toBe(0);
    expect(src.stdout).toContain('log from src');
    expect(src.stdout).not.toContain('---OLVE-RESULT');
    expect(src.stdout).toContain('src: succeeded');
    h.steps.build = () => ({
      envelope: { status: 'failed', error: { kind: 'handler', message: 'boom' } },
      exitCode: 1,
    });
    const build = await dev(['run', 'build', '--json'], h, p);
    expect(h.inputs.build).toEqual({ source: { sha: SHA }, commit: SHA });
    expect(build.code).toBe(1);
    expect(JSON.parse(build.stdout.trim()).error.message).toBe('boom');
    expect(build.stderr).toContain('log from build');
    const unknown = await dev(['run', 'nope'], h, p);
    expect(unknown.stderr).toContain("Unknown step 'nope'");
  });

  test('run --input-file is used verbatim; --input-file with --field is refused', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    const file = join(p.root, 'in.json');
    writeFileSync(file, '{"source":{"sha":"x"},"commit":"y"}');
    expect((await dev(['run', 'build', '--input-file', file], h, p)).code).toBe(0);
    expect(h.inputs.build).toEqual({ source: { sha: 'x' }, commit: 'y' });
    expect(
      (await dev(['run', 'build', '--input-file', file, '--field', 'sha=1'], h, p))
        .stderr,
    ).toContain('not both');
  });

  test('only the declared secrets are passed on; every other pipeline secret is unset', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    const result = await dev(
      [
        'run',
        'src',
        '--field',
        `sha=${SHA}`,
        '--secret',
        'TOKEN=t0k',
        '--secret',
        'SSH_KEY=k',
      ],
      h,
      p,
    );
    expect(result.code).toBe(0);
    const env = h.calls.find((c) => c.args[1] === 'run')!.env!;
    expect(env.TOKEN).toBe('t0k');
    expect(env.SSH_KEY).toBeNull();
    expect(env.OLVE_RESULT_URL).toBeNull();
    expect(env.OLVE_DIR).toBe(p.workspace.root);
    expect(result.stderr).toContain(
      "--secret SSH_KEY ignored: 'src' does not declare it",
    );
  });

  test('pipeline on beta chains the pool, skips the when block and propagates skips', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    h.steps.src = (input, olveDir) => {
      mkdirSync(join(olveDir, 'work', 'files'), { recursive: true });
      writeFileSync(join(olveDir, 'work', 'files', 'tree.tar'), 't');
      return {
        envelope: { status: 'succeeded', output: { sha: input.sha! } },
        exitCode: 0,
      };
    };
    const result = await dev(
      ['pipeline', '--field', `sha=${SHA}`, '--yes', '--json'],
      h,
      p,
    );
    expect(result.code).toBe(0);
    expect(h.ran()).toEqual(['src', 'build', 'lint', 'summary']);
    expect(h.inputs.build).toEqual({ source: { sha: SHA }, commit: SHA });
    expect('release' in h.inputs.summary!).toBe(false);
    const summary = JSON.parse(result.stdout.trim()) as {
      step: string;
      status: string;
      detail: string | null;
    }[];
    expect(summary.find((r) => r.step === 'notify')?.detail).toContain(
      'needs \'deploy\', which was skipped (when deployTo in ["prod"]',
    );
    expect(summary.find((r) => r.step === 'publish')?.status).toBe('skipped');
    expect(existsSync(join(p.workspace.poolFilesFor('src'), 'tree.tar'))).toBe(true);
  });

  test('pipeline on prod runs the when block; a missing required field fails before anything runs', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    expect(
      (
        await dev(
          ['pipeline', '--field', `sha=${SHA}`, '--field', 'deployTo=prod', '--yes'],
          h,
          p,
        )
      ).code,
    ).toBe(0);
    expect(h.ran()).toEqual(['src', 'build', 'lint', 'deploy', 'summary', 'notify']);
    const missing = await dev(
      ['pipeline', '--yes'],
      fakeHarness(demoText),
      pipelineDir(),
    );
    expect(missing.code).toBe(1);
    expect(missing.stderr).toContain(
      'missing required field(s): sha. Pass --field sha=…',
    );
  });

  test('a step with secrets asks first: yes runs, no stops, no terminal refuses without --yes', async () => {
    const yes = fakeHarness(demoText);
    const ran = await dev(['pipeline', '--field', `sha=${SHA}`], yes, pipelineDir(), {
      terminal: true,
      prompt: 'y',
    });
    expect(ran.code).toBe(0);
    expect(ran.questions[0]).toContain("'src' declares secrets (TOKEN)");
    const no = fakeHarness(demoText);
    const declined = await dev(['pipeline', '--field', `sha=${SHA}`], no, pipelineDir(), {
      terminal: true,
      prompt: 'n',
    });
    expect(declined.code).toBe(1);
    expect(declined.stderr).toContain("Stopped before 'src' (not confirmed).");
    expect(no.ran()).toEqual([]);
    const headless = await dev(
      ['pipeline', '--field', `sha=${SHA}`],
      fakeHarness(demoText),
      pipelineDir(),
    );
    expect(headless.stderr).toContain('pass --yes');
  });

  test('a failed step stops the run with its exit code; --only reuses the pool, a full run resets it', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    h.steps.build = () => ({
      envelope: { status: 'failed', error: { message: 'boom' } },
      exitCode: 65,
    });
    const failed = await dev(['pipeline', '--field', `sha=${SHA}`, '--yes'], h, p);
    expect(failed.code).toBe(65);
    expect(h.ran()).toEqual(['src', 'build']);
    delete h.steps.build;
    h.calls.length = 0;
    expect((await dev(['pipeline', '--only', 'build'], h, p)).code).toBe(0);
    expect(h.ran()).toEqual(['build']);
    expect(Pool.load(p.workspace.poolJson).steps.has('src')).toBe(true);
  });

  test('a config reaches every step as $config and names the selected case', async () => {
    const p = pipelineDir();
    const h = fakeHarness(selectText);
    const config = join(p.root, 'config.json');
    writeFileSync(config, JSON.stringify(SSH));
    expect(
      (await dev(['pipeline', '--field', 'sha=abc', '--config', config], h, p)).code,
    ).toBe(0);
    expect(h.ran()).toEqual(['build', 'deploy', 'summary']);
    expect(h.inputs.deploy!.$config).toEqual(SSH);
    expect(h.calls.find((c) => c.args[3] === 'deploy')!.env!.OLVE_CASES).toBe(
      '["target.kind=ssh"]',
    );
    expect(h.calls.find((c) => c.args[3] === 'build')!.env!.OLVE_CASES).toBeNull();
    writeFileSync(config, '[1]');
    expect((await dev(['run', 'build', '--config', config], h, p)).stderr).toContain(
      '--config must be a JSON object',
    );
  });

  test('--from seeds the pool from a run: its input, latest successful outputs, config and the files a step uses, fetched once', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    const api = fakeRunApi();
    const from = (argv: string[]) => dev(argv, h, p, { fetch: api.fetch });

    const publish = await from(['run', 'publish', '--from', 'demo#7']);
    expect(publish.code).toBe(1);
    expect(publish.stderr).toContain(
      "publish: input 'release' needs the output of 'deploy', which has none in demo#7; run `pl dev run deploy` first (or pass --input-file)",
    );
    const pool = Pool.load(p.workspace.poolJson);
    expect(pool.input).toEqual(RUN_INPUT);
    expect(pool.source).toEqual({
      pipeline: 'demo',
      pipelineId: ID,
      run: 7,
      config: SSH,
    });
    expect(Object.fromEntries(pool.steps)).toEqual({
      src: { status: 'succeeded', output: { sha: SHA }, reason: undefined },
      build: { status: 'succeeded', output: BUILD_OUTPUT, reason: undefined },
      lint: {
        status: 'skipped',
        output: undefined,
        reason: 'skipped in demo#7: no changes',
      },
    });
    expect(api.requests.some((r) => r.path.includes('/steps/src/output?attempt=1'))).toBe(
      true,
    );

    const deploy = await from(['run', 'deploy']);
    expect(deploy.code).toBe(0);
    expect(h.inputs.deploy).toEqual({ artifact: BUILD_OUTPUT, $config: SSH });
    const files = join(p.workspace.poolFiles, '7', 'build', '2', 'files');
    expect(readFileSync(join(files, 'app.bin'), 'utf8')).toBe('binary');
    expect(readFileSync(join(files, 'site.tar'), 'utf8')).toBe('tarball');
    expect(deploy.stderr).toContain('fetching demo#7/build@2/app.bin');
    expect(api.downloads()).toEqual(['app.bin?attempt=2', 'site/?attempt=2']);

    await from(['run', 'deploy']);
    expect(api.downloads()).toHaveLength(2);

    const beta = await from([
      'run',
      'src',
      '--from',
      'demo#7',
      '--field',
      'deployTo=beta',
    ]);
    expect(beta.code).toBe(0);
    expect(h.inputs.src).toEqual({
      sha: SHA,
      opts: { depth: 1, target: 'beta' },
      $config: SSH,
    });
    expect(existsSync(files)).toBe(false);

    h.calls.length = 0;
    const only = await from([
      'pipeline',
      '--from',
      'demo#7',
      '--only',
      'deploy',
      '--yes',
    ]);
    expect(only.code).toBe(0);
    expect(h.ran()).toEqual(['deploy']);
    expect(h.inputs.deploy).toEqual({ artifact: BUILD_OUTPUT, $config: SSH });

    h.calls.length = 0;
    const replay = await from([
      'pipeline',
      '--from',
      'demo#7',
      '--until',
      'src',
      '--yes',
    ]);
    expect(replay.code).toBe(0);
    expect(h.inputs.src!.opts).toEqual({ depth: 1, target: 'prod' });

    h.calls.length = 0;
    expect((await from(['pipeline', '--field', `sha=${SHA}`, '--yes'])).code).toBe(0);
    expect(h.inputs.src!.$config).toBeUndefined();
    expect(Pool.load(p.workspace.poolJson).source).toBeUndefined();
  });

  test("--from: a step rerun locally mixes its own files with the run's; only the run's are fetched", async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    const api = fakeRunApi();
    const local = {
      run: 0,
      step: 'build',
      attempt: 1,
      path: 'app.bin',
      sha256: sha('local'),
    };
    h.steps.build = () => ({
      envelope: {
        status: 'succeeded',
        output: { binary: local, assets: BUILD_OUTPUT.assets },
      },
      exitCode: 0,
    });
    const opts = { fetch: api.fetch };
    expect((await dev(['run', 'build', '--from', 'demo#7'], h, p, opts)).code).toBe(0);
    const deploy = await dev(['run', 'deploy'], h, p, opts);
    expect(deploy.code).toBe(0);
    expect(h.inputs.deploy!.artifact).toEqual({
      binary: local,
      assets: BUILD_OUTPUT.assets,
    });
    expect(api.downloads()).toEqual(['site/?attempt=2']);
  });

  const refusals: [string, string[], (api: RunApi) => void, string][] = [
    [
      'a file whose sha256 differs',
      ['run', 'deploy'],
      (api) => (api.content['app.bin'] = 'tampered'),
      `demo#7/build@2/app.bin has sha256 ${sha('tampered')}, expected ${sha('binary')}.`,
    ],
    [
      'a run without stages',
      ['run', 'deploy'],
      (api) => (api.run.stages = []),
      "demo#7 has no stages (its version's manifest is unavailable), so its outputs can't be used.",
    ],
    [
      'a file of another run',
      ['run', 'deploy'],
      (api) => (BUILD_OUTPUT_OF(api).binary.run = 6),
      'The input references demo#6/build@2/app.bin, which is not from a run in the local pool; pass --from <pipeline>#6.',
    ],
    [
      '--input-file with --from',
      ['run', 'deploy', '--input-file', 'in.json'],
      () => {},
      'Use either --input-file or --from, not both.',
    ],
  ];
  test.each(refusals)('--from refuses %s', async (_, argv, change, message) => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    const api = fakeRunApi();
    change(api);
    const result = await dev([...argv, '--from', 'demo#7'], h, p, { fetch: api.fetch });
    expect(result.code).not.toBe(0);
    expect(result.stderr).toContain(message);
    expect(h.ran()).toEqual([]);
  });
});

const sha = (text: string) => new Bun.CryptoHasher('sha256').update(text).digest('hex');
const RUN_INPUT = { sha: SHA, deployTo: 'prod', publish: false, replicas: 1 };
const fileRef = (path: string, content: string) => ({
  run: 7,
  step: 'build',
  attempt: 2,
  path,
  sha256: sha(content),
});
const BUILD_OUTPUT = {
  binary: fileRef('app.bin', 'binary'),
  assets: [{ site: fileRef('site/', 'tarball') }],
};
const BUILD_OUTPUT_OF = (api: RunApi) =>
  api.outputs.build as unknown as typeof BUILD_OUTPUT;

type RunApi = ReturnType<typeof fakeRunApi>;

/** `demo#7` on deployTo=prod: src succeeded on attempt 1 (its rerun failed), build on attempt 2, lint skipped, the rest not reached. */
function fakeRunApi() {
  const step = (
    name: string,
    status: string,
    succeededAttempt: number | null,
    reason?: string,
  ) => ({
    name,
    stepVersion: 1,
    status,
    reason: reason ?? null,
    attemptCount: 2,
    latestAttempt: 2,
    succeededAttempt,
    startedAt: null,
    finishedAt: null,
    blocked: false,
  });
  const run = {
    number: 7,
    input: RUN_INPUT,
    config: 'target:\n  kind: ssh\n  host: box\n',
    stages: [
      { index: 0, when: [], steps: [step('src', 'failed', 1, 'boom')] },
      {
        index: 1,
        when: [],
        steps: [
          step('build', 'succeeded', 2),
          step('lint', 'skipped', null, 'no changes'),
        ],
      },
      { index: 2, when: [], steps: [step('deploy', 'not-run', null)] },
    ] as unknown[],
  };
  const outputs: Record<string, JsonObject> = {
    src: { sha: SHA },
    build: structuredClone(BUILD_OUTPUT) as unknown as JsonObject,
  };
  const content: Record<string, string> = { 'app.bin': 'binary', 'site/': 'tarball' };
  const base = `/api/pipelines/${ID}/runs/7`;
  const { fetch, requests } = fakeFetch((req: Recorded) => {
    if (req.path === '/api/pipelines') return { body: [{ id: ID, name: 'demo' }] };
    if (req.path === base) return { body: run };
    const output = /\/steps\/([^/]+)\/output\?attempt=(\d+)$/.exec(req.path);
    if (output)
      return {
        body: {
          run: 7,
          step: output[1],
          attempt: Number(output[2]),
          status: 'succeeded',
          output: outputs[output[1]!] ?? null,
          outputKey: null,
        },
      };
    const file = /\/steps\/build\/files\/(.+)\?attempt=2$/.exec(req.path);
    if (file && content[decodeURIComponent(file[1]!)] !== undefined)
      return {
        body: content[decodeURIComponent(file[1]!)],
        headers: { 'Content-Type': 'application/octet-stream' },
      };
    return { status: 404 };
  });
  const downloads = () =>
    requests
      .filter((r) => r.path.includes('/files/'))
      .map((r) => decodeURIComponent(r.path.split('/files/')[1]!));
  return { fetch, requests, run, outputs, content, downloads };
}

describe('pl dev matrix steps', () => {
  const built = (h: ReturnType<typeof fakeHarness>) =>
    h.calls.filter((c) => c.args[3] === 'build-cli').map((c) => c.env?.OLVE_CELL);
  const byCell: Script = (input, _dir, env) => {
    const cell = JSON.parse(env.OLVE_CELL!) as Record<string, string>;
    return {
      envelope: {
        status: 'succeeded',
        output: { name: `${input.sha}-${cell.os}-${cell.arch}` },
      },
      exitCode: 0,
    };
  };
  test('pipeline runs every cell in order; a cells input reads them in cell order', async () => {
    const p = pipelineDir();
    const h = fakeHarness(matrixText);
    h.steps['build-cli'] = byCell;
    const r = await dev(['pipeline', '--field', 'sha=abc', '--json'], h, p);
    expect(r.code).toBe(0);
    expect(built(h)).toEqual([
      '{"os":"linux","arch":"x64"}',
      '{"os":"linux","arch":"arm64"}',
      '{"os":"windows","arch":"x64"}',
    ]);
    expect(h.inputs.publish).toEqual({
      binaries: [
        { cell: { os: 'linux', arch: 'x64' }, output: { name: 'abc-linux-x64' } },
        { cell: { os: 'linux', arch: 'arm64' }, output: { name: 'abc-linux-arm64' } },
        { cell: { os: 'windows', arch: 'x64' }, output: { name: 'abc-windows-x64' } },
      ],
    });
    expect(
      (JSON.parse(r.stdout.trim()) as { step: string }[]).map((x) => x.step),
    ).toEqual([
      'build-cli[os=linux,arch=x64]',
      'build-cli[os=linux,arch=arm64]',
      'build-cli[os=windows,arch=x64]',
      'publish',
    ]);
    expect(h.calls.find((c) => c.args[3] === 'publish')!.env!.OLVE_CELL).toBeNull();
    expect((await dev(['describe'], h, p)).stdout).toContain(
      'build-cli            bun-img -> BuiltBinary  [matrix: os × arch, 3 cells]',
    );
  });
  test('a failed cell lets the other cells run, then stops the pipeline', async () => {
    const p = pipelineDir();
    const h = fakeHarness(matrixText);
    h.steps['build-cli'] = (input, dir, env) =>
      env.OLVE_CELL === '{"os":"linux","arch":"arm64"}'
        ? { envelope: { status: 'failed', error: { message: 'boom' } }, exitCode: 1 }
        : byCell(input, dir, env);
    const r = await dev(['pipeline', '--field', 'sha=abc'], h, p);
    expect(r.code).toBe(1);
    expect(h.ran()).toEqual(['build-cli', 'build-cli', 'build-cli']);
    expect(r.stderr).toContain("'build-cli' failed; stopping");
    const publish = await dev(['run', 'publish'], h, p);
    expect(publish.stderr).toContain(
      "publish: input 'binaries' needs the output of 'build-cli[os=linux,arch=arm64]', which failed (boom); rerun `pl dev run build-cli --cell os=linux,arch=arm64`",
    );
  });
  test('run --cell picks cells; a value that matches none, or a plain step, is refused', async () => {
    const p = pipelineDir();
    const h = fakeHarness(matrixText);
    h.steps['build-cli'] = byCell;
    const before = await dev(['run', 'publish'], h, p);
    expect(before.stderr).toContain(
      "publish: input 'binaries' needs the output of 'build-cli', which is not in the local pool; run `pl dev run build-cli` first",
    );
    const r = await dev(
      ['run', 'build-cli', '--cell', 'os=linux', '--field', 'sha=abc'],
      h,
      p,
    );
    expect(r.code).toBe(0);
    expect(built(h)).toEqual([
      '{"os":"linux","arch":"x64"}',
      '{"os":"linux","arch":"arm64"}',
    ]);
    expect(r.stdout).toContain('build-cli[os=linux,arch=arm64]: succeeded');
    expect((await dev(['run', 'publish'], h, p)).code).toBe(0);
    expect(h.inputs.publish!.binaries).toHaveLength(2);
    const none = await dev(['run', 'build-cli', '--cell', 'os=solaris'], h, p);
    expect(none.stderr).toContain(
      "--cell os=solaris matches no cell of 'build-cli' (cells: build-cli[os=linux,arch=x64], build-cli[os=linux,arch=arm64], build-cli[os=windows,arch=x64]).",
    );
    const plain = await dev(['run', 'publish', '--cell', 'os=linux'], h, p);
    expect(plain.stderr).toContain("--cell: 'publish' is not a matrix step.");
  });
});
