// checks(): a pipeline's CI, described into the manifest's `ci` section and run by the harness.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterAll, describe, expect, test } from 'bun:test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  checks,
  external,
  implement,
  model,
  person,
  pipeline,
  secret,
  step,
  z,
} from '../../src/index.ts';
import { describePipeline, DescribeError } from '../../src/describe/describe.ts';
import { Manifest } from '../../src/describe/manifest-schema.ts';
import { runHarness } from '../../src/harness/main.ts';
import { devConfig } from '../../src/dev/cli.ts';
import { SDK, captureIO, removeTmpDirs, tmp, withEnv } from './helpers.ts';

afterAll(removeTmpDirs);

const harness = { bun: '1.4.3', sdk: '0.0.0', sdkHash: 'sha256:' + 'a'.repeat(64) };
const Req = model('Req', z.object({ sha: z.string() }));
const Src = model('Src', z.object({ sha: z.string() }));
const TOKEN = secret('TOKEN', 'Reads the repo.');
const DEPLOY_KEY = secret('DEPLOY_KEY', 'Deploys.');

const source = implement(
  step({
    name: 'source',
    description: 'Fetches the commit.',
    image: 'alpine',
    input: { sha: z.string() },
    output: Src,
    secrets: [TOKEN],
  }),
  ({ sha }) => ({ sha }),
);
const test_ = implement(
  step({ name: 'test', description: '', image: 'alpine', input: { src: Src } }),
  () => undefined,
);
const lint = implement(
  step({ name: 'lint', description: '', image: 'alpine', input: { src: Src } }),
  (_, ctx) => void ctx.log('linted'),
);
const deploy = implement(
  step({
    name: 'deploy',
    description: '',
    image: 'alpine',
    input: { src: Src },
    secrets: [DEPLOY_KEY],
  }),
  () => undefined,
);

const cd = () =>
  pipeline({ name: 'app', input: Req })
    .stage([source, (from) => ({ sha: from.input.sha })])
    .stage(test_)
    .stage(deploy);
const ci = () =>
  checks({ description: 'Checks a pull request.', secrets: [TOKEN] })
    .stage([source, (from) => ({ sha: from.input.sha })])
    .stage(test_, lint);

const describeWith = (c: unknown, p: unknown = cd()) =>
  describePipeline(p, { harness, ci: c }) as any;
const problemsOf = (c: unknown, p: unknown = cd()) => {
  try {
    describeWith(c, p);
  } catch (e) {
    if (e instanceof DescribeError) return e.problems;
    throw e;
  }
  return [];
};

describe('the ci section', () => {
  test('describes CI next to the pipeline, against CiInput', () => {
    const m = describeWith(ci());
    expect(Manifest.safeParse(m).success).toBe(true);
    expect(m.ci).toMatchObject({
      description: 'Checks a pull request.',
      input: 'CiInput',
      secrets: ['TOKEN'],
    });
    expect(m.ci.stages.map((s: any) => s.steps.map((d: any) => d.name))).toEqual([
      ['source'],
      ['test', 'lint'],
    ]);
    expect(m.ci.stages[0].steps[0].input).toEqual({ sha: { ref: 'input.sha' } });
    expect(Object.keys(m.models)).toContain('CiInput');
  });

  test('a pipeline without a ci export has no ci section', () => {
    expect(describePipeline(cd(), { harness })).not.toHaveProperty('ci');
  });

  const rows: [string, () => unknown, () => unknown, string][] = [
    [
      'a CI step declaring a secret outside the allow-list',
      () => checks().stage([source, (from) => ({ sha: from.input.sha })]),
      cd,
      "CI step 'source' uses secret 'TOKEN', which is not in ci.secrets",
    ],
    [
      'an allow-listed secret no step declares',
      () =>
        checks({ secrets: [TOKEN, secret('OTHER', 'x')] }).stage([
          source,
          (from) => ({ sha: from.input.sha }),
        ]),
      cd,
      "ci.secrets lists 'OTHER', which is not in the pipeline's secrets",
    ],
    [
      'a CI step that waits for external input',
      () =>
        checks().stage(
          implement(
            step({
              name: 'approve',
              description: '',
              image: 'alpine',
              input: {
                ok: external(model('Ok', z.object({ ok: z.boolean() })), {
                  title: 'Approve',
                  from: [person()],
                }),
              },
            }),
            () => undefined,
          ),
        ),
      cd,
      "CI step 'approve' waits for external inputs; nobody answers a pull request's step",
    ],
    [
      'a CI step that asks for cluster access',
      () =>
        checks().stage(
          implement(
            step({
              name: 'deploy',
              description: '',
              image: 'alpine',
              clusterAccess: true,
            }),
            () => undefined,
          ),
        ),
      cd,
      "CI step 'deploy' asks for cluster access; a pull request's code never gets it",
    ],
    [
      'a step in both that declares something else',
      () =>
        checks({ secrets: [TOKEN] })
          .stage([source, (from) => ({ sha: from.input.sha })])
          .stage(
            implement(
              step({
                name: 'test',
                description: '',
                image: 'debian',
                input: { src: Src },
              }),
              () => undefined,
            ),
          ),
      cd,
      "CI step 'test' differs from the pipeline's step 'test'; a step in both must be the same step",
    ],
    [
      'a step in both with another implementation',
      () =>
        checks({ secrets: [TOKEN] })
          .stage([source, (from) => ({ sha: from.input.sha })])
          .stage(
            implement(
              step({
                name: 'test',
                description: '',
                image: 'alpine',
                input: { src: Src },
              }),
              () => {
                throw new Error('other');
              },
            ),
          ),
      cd,
      "CI step 'test' has a different implementation than the pipeline's step 'test'; a step in both must be the same step",
    ],
    [
      'a ci export that is not checks()',
      cd,
      cd,
      'the `ci` export must be checks({...}).stage(...)',
    ],
    [
      'a when on a field CiInput lacks',
      () => checks().when({ pr: 1 } as any, (p) => p.stage(lint as any)),
      cd,
      "when: 'pr' is not a field of the CI input CiInput",
    ],
  ];
  test.each(rows)('%s is a describe problem', (_, c, p, message) => {
    expect(problemsOf(c(), p())).toContain(message);
  });

  test('a checks() value as the default export is a describe problem', () => {
    expect(() => describePipeline(ci(), { harness })).toThrow(
      'the default export is checks({...}); default-export the pipeline and export its CI as `ci`',
    );
  });
});

describe('the harness', () => {
  test('runs a step only CI has', async () => {
    const dir = tmp('checks-run');
    writeFileSync(join(dir, 'input.json'), JSON.stringify({ src: { sha: 'abc' } }));
    const io = captureIO();
    const code = await withEnv({ OLVE_DIR: dir, OLVE_RESULT_URL: undefined }, () =>
      runHarness({ pipeline: cd(), ci: ci(), harness }, ['run', 'lint'], io),
    );
    expect(code).toBe(0);
    expect(io.err.join('\n')).toContain('linted');
  });
});

describe('a pipeline package', () => {
  test("describes the entry point's ci export from another file", async () => {
    const dir = tmp('checks-package');
    mkdirSync(join(dir, 'node_modules/@olivervea'), { recursive: true });
    Bun.spawnSync(['ln', '-s', SDK, join(dir, 'node_modules/@olivervea/pipelines')]);
    writeFileSync(
      join(dir, 'package.json'),
      JSON.stringify({ name: 'p', type: 'module', main: 'main.ts' }),
    );
    writeFileSync(
      join(dir, 'steps.ts'),
      `import { implement, model, step, z } from '@olivervea/pipelines';
export const Src = model('Src', z.object({ sha: z.string() }));
export const source = implement(step({ name: 'source', description: '', image: 'alpine', input: { sha: z.string() }, output: Src }), ({ sha }) => ({ sha }));
export const lint = implement(step({ name: 'lint', description: '', image: 'alpine', input: { src: Src } }), () => undefined);
`,
    );
    writeFileSync(
      join(dir, 'ci.ts'),
      `import { checks } from '@olivervea/pipelines';
import { lint, source } from './steps';
export const ci = checks().stage([source, (from) => ({ sha: from.input.sha })]).stage(lint);
`,
    );
    writeFileSync(
      join(dir, 'main.ts'),
      `import { model, pipeline, z } from '@olivervea/pipelines';
import { source } from './steps';
export { ci } from './ci';
export default pipeline({ name: 'p', input: model('Req', z.object({ sha: z.string() })) })
  .stage([source, (from) => ({ sha: from.input.sha })]);
`,
    );
    const cfg = await devConfig(dir, false);
    const io = captureIO();
    expect(await runHarness(cfg, ['describe'], io)).toBe(0);
    const m = JSON.parse(io.out);
    expect(m.ci.stages.flatMap((s: any) => s.steps.map((d: any) => d.name))).toEqual([
      'source',
      'lint',
    ]);
  });
});
