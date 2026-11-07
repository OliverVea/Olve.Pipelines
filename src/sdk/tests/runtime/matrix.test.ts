// Matrix steps: the manifest's matrix and cells inputs, their describe-time checks, OLVE_CELL in
// the harness and testHandler. Rejected pipelines are built untyped on purpose.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, test } from 'bun:test';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  cascadeFailure,
  cells,
  fileRef,
  implement,
  keepRunning,
  model,
  pipeline,
  step,
  z,
} from '../../src/index.ts';
import { testHandler } from '../../src/testing/index.ts';
import { describePipeline, DescribeError } from '../../src/describe/describe.ts';
import { runHarness } from '../../src/harness/main.ts';
import { EXIT } from '../../src/harness/exit.ts';
import { captureIO, tmp, withEnv } from './helpers.ts';

const harness = { bun: '1.4.3', sdk: '0.0.0', sdkHash: 'sha256:' + 'a'.repeat(64) };
const Req = model(
  'Req',
  z.object({ sha: z.string(), env: z.enum(['beta', 'prod']).default('beta') }),
);
const BuiltBinary = model(
  'BuiltBinary',
  z.object({ name: z.string(), bin: fileRef().optional() }),
);
const Other = model('Other', z.object({ n: z.number() }));

const axes = { os: ['linux', 'windows', 'macos'], arch: ['x64', 'arm64'] } as const;
const buildCli = implement(
  step({
    name: 'build-cli',
    description: '',
    image: 'alpine',
    input: { req: Req },
    output: BuiltBinary,
    matrix: {
      axes,
      exclude: [{ os: 'windows', arch: 'arm64' }],
      failure: cascadeFailure(),
      maxParallel: 2,
    },
  }),
  async ({ req }, ctx) => {
    const name = `${req.sha}-${ctx.cell.os}-${ctx.cell.arch}`;
    const bin = await ctx.output.write('bin', name);
    ctx.log('built', { step: ctx.run.step });
    return { name, bin };
  },
);
const publish = implement(
  step({
    name: 'publish',
    description: '',
    image: 'alpine',
    input: { binaries: cells(BuiltBinary) },
  }),
  ({ binaries }, ctx) => {
    ctx.log(binaries.map((b) => b.output.name).join(' '));
  },
);

const base = () => pipeline({ name: 't', input: Req }) as any;
const s = (def: any): any =>
  implement(step({ description: '', image: 'alpine', ...def }), (() => {}) as never);
const mx = (def: any, matrix: any = { axes: { os: ['linux', 'windows'] } }) =>
  s({ ...def, matrix });
const stepsOf = (m: any): any[] =>
  m.stages.flatMap((x: any) => ('steps' in x ? x.steps : []));
function problems(p: unknown): string[] {
  try {
    describePipeline(p, { harness });
  } catch (e) {
    if (e instanceof DescribeError) return [...e.problems];
    throw e;
  }
  return [];
}

describe('manifest', () => {
  test('a matrix step and a cells input', () => {
    const m = describePipeline(base().stage(buildCli).stage(publish), { harness }) as any;
    const [b, p] = stepsOf(m);
    expect(b).toEqual({
      name: 'build-cli',
      description: '',
      kind: 'custom',
      image: 'alpine',
      inputTypes: { req: { model: 'Req', optional: false } },
      input: { req: { ref: 'input' } },
      output: 'BuiltBinary',
      secrets: [],
      config: {},
      matrix: {
        axes: [
          { name: 'os', values: ['linux', 'windows', 'macos'] },
          { name: 'arch', values: ['x64', 'arm64'] },
        ],
        exclude: [{ arch: 'arm64', os: 'windows' }],
        failure: { kind: 'cascadeFailure' },
        maxParallel: 2,
      },
    });
    expect(p.inputTypes).toEqual({
      binaries: { model: 'BuiltBinary', optional: false, cells: true },
    });
    expect(p.input).toEqual({ binaries: { ref: 'build-cli' } });
  });
  test('failure defaults to keepRunning; exclude and maxParallel are omitted when unset', () => {
    const m = describePipeline(base().stage(mx({ name: 'a' })), { harness }) as any;
    expect(stepsOf(m)[0].matrix).toEqual({
      axes: [{ name: 'os', values: ['linux', 'windows'] }],
      failure: { kind: 'keepRunning' },
    });
  });
  test('a cells input wired with a mapper', () => {
    const m = describePipeline(
      base()
        .stage(buildCli)
        .stage([publish, (f: any) => ({ binaries: f['build-cli'] })]),
      { harness },
    ) as any;
    expect(stepsOf(m)[1].input).toEqual({ binaries: { ref: 'build-cli' } });
  });
});

describe('describe-time checks', () => {
  const at = "step 'a'";
  const one = s({ name: 'one', input: { b: BuiltBinary } });
  const all = s({ name: 'all', input: { b: cells(BuiltBinary) } });
  const make = (name: string) => mx({ name, output: BuiltBinary });
  const plainMake = s({ name: 'plain', output: BuiltBinary });
  const refused: [string, () => unknown, string[]][] = [
    [
      'no axes',
      () => base().stage(mx({ name: 'a' }, { axes: {} })),
      [`${at}: a matrix has 1 to 4 axes (this one has 0)`],
    ],
    [
      'five axes',
      () =>
        base().stage(
          mx(
            { name: 'a' },
            { axes: { a: ['1'], b: ['1'], c: ['1'], d: ['1'], e: ['1'] } },
          ),
        ),
      [`${at}: a matrix has 1 to 4 axes (this one has 5)`],
    ],
    [
      'an axis name',
      () => base().stage(mx({ name: 'a' }, { axes: { OS: ['linux'] } })),
      [`${at}: matrix axis name 'OS' must match /^[a-z][a-zA-Z0-9]{0,30}$/`],
    ],
    [
      'an axis without values',
      () => base().stage(mx({ name: 'a' }, { axes: { os: [] } })),
      [`${at}: matrix axis 'os' has no values`],
    ],
    [
      'an axis that is not a list of strings',
      () => base().stage(mx({ name: 'a' }, { axes: { os: [1] } })),
      [`${at}: matrix axis 'os' must be a list of strings`],
    ],
    [
      'an axis value',
      () => base().stage(mx({ name: 'a' }, { axes: { os: ['linux', 'mac os'] } })),
      [
        `${at}: matrix axis 'os': value 'mac os' must match /^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$/`,
      ],
    ],
    [
      'a repeated value',
      () => base().stage(mx({ name: 'a' }, { axes: { os: ['linux', 'linux'] } })),
      [`${at}: matrix axis 'os': value 'linux' is listed twice`],
    ],
    [
      'exclude entries',
      () =>
        base().stage(
          mx(
            { name: 'a' },
            {
              axes: { os: ['linux', 'windows'] },
              exclude: [{}, { arch: 'x64' }, { os: 'solaris' }],
            },
          ),
        ),
      [
        `${at}: matrix exclude 1 must name at least one axis`,
        `${at}: matrix exclude 2: 'arch' is not an axis`,
        `${at}: matrix exclude 3: 'solaris' is not a value of axis 'os'`,
      ],
    ],
    [
      'excluding every cell',
      () =>
        base().stage(
          mx({ name: 'a' }, { axes: { os: ['linux'] }, exclude: [{ os: 'linux' }] }),
        ),
      [`${at}: the matrix excludes every cell`],
    ],
    [
      'more than 64 cells',
      () =>
        base().stage(
          mx(
            { name: 'a' },
            {
              axes: {
                a: ['1', '2', '3', '4', '5'],
                b: ['1', '2', '3', '4'],
                c: ['1', '2', '3', '4'],
              },
            },
          ),
        ),
      [`${at}: the matrix has 80 cells (at most 64)`],
    ],
    [
      'a failure strategy',
      () =>
        base().stage(
          mx({ name: 'a' }, { axes: { os: ['linux'] }, failure: { kind: 'failFast' } }),
        ),
      [`${at}: matrix failure must be keepRunning() or cascadeFailure()`],
    ],
    [
      'maxParallel',
      () => base().stage(mx({ name: 'a' }, { axes: { os: ['linux'] }, maxParallel: 0 })),
      [`${at}: matrix maxParallel must be a positive integer`],
    ],
    [
      'a matrix failure handler',
      () =>
        base()
          .stage(s({ name: 'x' }))
          .onFailure(mx({ name: 'a' })),
      [`${at}: a failure handler cannot be a matrix step`],
    ],
    [
      'a plain input from a matrix step',
      () => base().stage(make('m')).stage(one),
      [
        "step 'one': BuiltBinary (input 'b') is produced by matrix step 'm'; read it with cells(BuiltBinary)",
      ],
    ],
    [
      'a cells input from a plain step',
      () => base().stage(plainMake).stage(all),
      [
        "step 'all': BuiltBinary (input 'b') has no matrix step producing it at this stage; cells() reads a matrix step",
      ],
    ],
    [
      'a cells input with several matrix producers',
      () => base().stage(make('m'), make('n')).stage(all),
      [
        "step 'all': BuiltBinary (input 'b') has several matrix steps producing it (m, n); use a mapper to pick one",
      ],
    ],
    [
      'a cells input from a maybe-skipped matrix step',
      () =>
        base()
          .when({ env: 'prod' }, (p: any) => p.stage(make('m')))
          .stage(all),
      [
        "step 'all': BuiltBinary (input 'b') is only maybe-available (produced inside a when block); cells() cannot read a maybe-skipped matrix step",
      ],
    ],
    [
      'a field of a matrix step in a mapper',
      () =>
        base()
          .stage(make('m'))
          .stage([one, (f: any) => ({ b: { name: f.m.name } })]),
      [
        "step 'one' input 'b'.name: 'm.name' — 'm' is a matrix step; read it whole through a cells() input",
      ],
    ],
    [
      'a matrix step mapped into a plain input',
      () =>
        base()
          .stage(make('m'))
          .stage([one, (f: any) => ({ b: f.m })]),
      [
        "step 'one' input 'b': 'm' — 'm' is a matrix step; read it whole through a cells() input",
      ],
    ],
    [
      'a plain step mapped into a cells input',
      () =>
        base()
          .stage(plainMake)
          .stage([all, (f: any) => ({ b: f.plain })]),
      ["step 'all' input 'b': 'plain' is not a matrix step; cells() reads a matrix step"],
    ],
    [
      'a field mapped into a cells input',
      () =>
        base()
          .stage(make('m'))
          .stage([all, (f: any) => ({ b: f.m.name })]),
      [
        "step 'all' input 'b': a cells() input takes a whole matrix step (from['<step>']), not a field or an object",
      ],
    ],
    [
      'a matrix step of another model mapped into a cells input',
      () =>
        base()
          .stage(mx({ name: 'm', output: Other }))
          .stage([all, (f: any) => ({ b: f.m })]),
      ["step 'all' input 'b': 'm' outputs Other, not BuiltBinary"],
    ],
  ];
  test.each(refused)('refuses %s', (_name, p, expected) =>
    expect(problems(p())).toEqual(expected),
  );
  test('accepts a matrix step read by model next to a plain producer of the same model', () =>
    expect(problems(base().stage(make('m'), plainMake).stage(one, all))).toEqual([]));
});

async function runCell(p: unknown, name: string, cell: string | undefined) {
  const dir = tmp('matrix');
  writeFileSync(
    join(dir, 'input.json'),
    JSON.stringify({ req: { sha: 'abc', env: 'beta' } }),
  );
  const io = captureIO();
  const code = await withEnv(
    { OLVE_DIR: dir, OLVE_RESULT_URL: undefined, OLVE_CELL: cell },
    () => runHarness({ pipeline: p, harness }, ['run', name], io),
  );
  const out = join(dir, 'work', 'output.json');
  const envelope = existsSync(out) ? JSON.parse(readFileSync(out, 'utf8')) : undefined;
  return { code, io, envelope };
}

describe('run <step>', () => {
  const p = base().stage(buildCli);
  test('runs the cell OLVE_CELL names; its files carry the cell name', async () => {
    const r = await runCell(p, 'build-cli', '{"arch":"x64","os":"linux"}');
    expect(r.code).toBe(EXIT.ok);
    expect(r.envelope).toMatchObject({
      status: 'succeeded',
      output: {
        name: 'abc-linux-x64',
        bin: { step: 'build-cli[os=linux,arch=x64]', path: 'bin' },
      },
    });
    expect(r.io.err).toContain('built step=build-cli[os=linux,arch=x64]');
  });
  const cellsList =
    'build-cli[os=linux,arch=x64], build-cli[os=linux,arch=arm64], build-cli[os=windows,arch=x64], build-cli[os=macos,arch=x64], build-cli[os=macos,arch=arm64]';
  test.each([
    [
      'missing',
      undefined,
      `step 'build-cli' is a matrix step: OLVE_CELL must name the cell to run, e.g. {"os":"linux","arch":"x64"}`,
    ],
    [
      'not JSON',
      'os=linux',
      `OLVE_CELL is not JSON: JSON Parse error: Unexpected identifier "os"`,
    ],
    [
      'not an object',
      '["linux"]',
      'OLVE_CELL must be a JSON object of axis values, e.g. {"os":"linux","arch":"x64"}',
    ],
    [
      'excluded',
      '{"os":"windows","arch":"arm64"}',
      `OLVE_CELL {"os":"windows","arch":"arm64"} is not a cell of step 'build-cli' (cells: ${cellsList})`,
    ],
    [
      'missing an axis',
      '{"os":"linux"}',
      `OLVE_CELL {"os":"linux"} is not a cell of step 'build-cli' (cells: ${cellsList})`,
    ],
    [
      'an extra axis',
      '{"os":"linux","arch":"x64","gpu":"none"}',
      `OLVE_CELL {"os":"linux","arch":"x64","gpu":"none"} is not a cell of step 'build-cli' (cells: ${cellsList})`,
    ],
  ])('OLVE_CELL %s fails with exit 65', async (_name, cell, message) => {
    const r = await runCell(p, 'build-cli', cell);
    expect(r.code).toBe(EXIT.inputInvalid);
    expect(r.envelope).toEqual({ status: 'failed', error: { kind: 'input', message } });
  });
});

describe('testHandler', () => {
  test('runs a cell, and a downstream step reads cells', async () => {
    const r = await testHandler(
      buildCli,
      { req: { sha: 'abc', env: 'beta' } },
      { cell: { os: 'macos', arch: 'arm64' } },
    );
    expect(r).toMatchObject({ status: 'succeeded', output: { name: 'abc-macos-arm64' } });
    const q = await testHandler(publish, {
      binaries: [
        { cell: { os: 'linux' }, output: { name: 'a' } },
        { cell: { os: 'macos' }, output: { name: 'b' } },
      ],
    });
    expect(q).toMatchObject({ status: 'succeeded', logs: ['a b'] });
  });
  test('a matrix step without a cell fails like a pod without OLVE_CELL', async () => {
    const r = await testHandler(buildCli, { req: { sha: 'abc', env: 'beta' } });
    expect(r).toMatchObject({
      status: 'failed',
      exitCode: EXIT.inputInvalid,
      error: {
        kind: 'input',
        message: `step 'build-cli' is a matrix step: OLVE_CELL must name the cell to run, e.g. {"os":"linux","arch":"x64"}`,
      },
    });
  });
  test('a cells input is validated against its model', async () => {
    const r = await testHandler(publish, {
      binaries: [{ cell: { os: 'linux' }, output: { name: 1 } as never }],
    });
    expect(r.status).toBe('failed');
    expect(r.exitCode).toBe(EXIT.inputInvalid);
  });
});

test('keepRunning() and cascadeFailure() are their kinds', () => {
  expect(keepRunning()).toEqual({ kind: 'keepRunning' });
  expect(cascadeFailure()).toEqual({ kind: 'cascadeFailure' });
});
