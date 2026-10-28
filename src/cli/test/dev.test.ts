import { describe, expect, test } from 'bun:test';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { assemble } from '../src/dev/assemble';
import { parseManifest, type JsonObject } from '../src/dev/manifest';
import { flatten, placeStep, select, whenMismatch } from '../src/dev/plan';
import { Pool, Workspace } from '../src/dev/pool';
import {
  buildPipelineInput,
  missingRequired,
  parseList,
  parseSecrets,
} from '../src/commands/dev';
import type { ProcessResult, ProcessSpec } from '../src/process';
import { pl } from './helpers';

const demoText = readFileSync(
  join(import.meta.dir, 'fixtures/demo.manifest.json'),
  'utf8',
);
const selectText = readFileSync(
  join(import.meta.dir, 'fixtures/select.manifest.json'),
  'utf8',
);
const demo = () => parseManifest(demoText);
const SHA = '0123456789abcdef0123456789abcdef01234567';
const SSH: JsonObject = { target: { kind: 'ssh', host: 'box' } };

type Script = (
  input: JsonObject,
  olveDir: string,
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
      const { envelope, exitCode } = steps[step]?.(input, olveDir) ?? {
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
  writeFileSync(join(dir, 'pipeline.ts'), '// fixture\n');
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
  extra: { prompt?: string; terminal?: boolean; env?: Record<string, string> } = {},
) {
  const questions: string[] = [];
  const result = await pl(['dev', ...argv, '--dir', p.dir], {
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
    expect(assemble(step('build'), pool).problems[0]).toContain(
      'whose last local run failed',
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

  test('a missing pipeline.ts or SDK says what to do', async () => {
    const p = pipelineDir();
    const h = fakeHarness(demoText);
    expect(
      (await pl(['dev', 'describe', '--dir', p.root], { run: h.run, env: p.env })).stderr,
    ).toContain('No pipeline.ts');
    const noSdk = await pl(['dev', 'describe', '--dir', p.dir], {
      run: h.run,
      env: { OLVE_BUN: process.execPath },
    });
    expect(noSdk.stderr).toContain('run `bun install` there');
  });

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
});
