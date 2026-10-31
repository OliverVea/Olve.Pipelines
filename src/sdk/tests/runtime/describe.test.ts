// Describe-time checks and manifest shape. Pipelines are built in-test; the type errors they
// contain are deliberate (describe must catch what TypeScript was told to ignore).
/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  bearer,
  hmacSha256,
  implement,
  model,
  optional,
  pipeline,
  secret,
  step,
  steps,
  z,
  Source,
} from '../../src/index.ts';
import { describePipeline, DescribeError } from '../../src/describe/describe.ts';
import { Manifest, manifestJsonSchema } from '../../src/describe/manifest-schema.ts';
import { canonicalJson } from '../../src/util/canonical.ts';
import { devConfig } from '../../src/dev/cli.ts';
import { allSteps } from '../../src/harness/main.ts';
import { pipelineData } from '../../src/builder/pipeline.ts';
import { EXAMPLE, SDK } from './helpers.ts';

const harness = { bun: '1.4.3', sdk: '0.0.0', sdkHash: 'sha256:' + 'a'.repeat(64) };
const Req = model(
  'Req',
  z.object({
    sha: z.string(),
    env: z.enum(['beta', 'prod']).default('beta'),
    flag: z.boolean().default(false),
    n: z.number(),
  }),
);
const Art = model(
  'Art',
  z.object({ version: z.string(), nested: z.object({ a: z.string() }) }),
);
const Dep = model('Dep', z.object({ url: z.string() }));
const make = step({
  name: 'make',
  description: '',
  image: 'alpine',
  input: { req: Req },
  output: Art,
});
const make2 = step({
  name: 'make2',
  description: '',
  image: 'alpine',
  input: { req: Req },
  output: Art,
});
const use = step({
  name: 'use',
  description: '',
  image: 'alpine',
  input: { art: Art },
  output: Dep,
});

/** Problems of `p`; by default every custom step has a handler. */
function problems(p: unknown, names?: string[]): string[] {
  const handlerNames =
    names ??
    allSteps(pipelineData(p))
      .filter((s) => s.kind === 'custom')
      .map((s) => s.name);
  try {
    describePipeline(p, {
      handlerNames,
      harness,
    });
  } catch (e) {
    if (e instanceof DescribeError) return [...e.problems];
    throw e;
  }
  return [];
}
const base = () => pipeline({ name: 't', input: Req });
const expectProblem = (p: unknown, re: RegExp, names?: string[]) => {
  const ps = problems(p, names);
  expect(
    ps.some((x) => re.test(x)),
    `expected a problem matching ${re}, got:\n${ps.join('\n')}`,
  ).toBe(true);
};

describe('example manifest', async () => {
  const cfg = await devConfig(EXAMPLE, true);
  const m = describePipeline(cfg.pipeline, {
    handlerNames: Object.keys(cfg.handlers),
    codeHash: cfg.codeHash,
    harness: cfg.harness,
  });

  test('validates against the manifest schema', () =>
    expect(Manifest.safeParse(m).success).toBe(true));
  test('is deterministic', () => {
    const again = describePipeline(cfg.pipeline, {
      handlerNames: Object.keys(cfg.handlers),
      codeHash: cfg.codeHash,
      harness: cfg.harness,
    });
    expect(canonicalJson(again)).toBe(canonicalJson(m));
  });
  test('step descriptions', () => {
    const [s0, s1] = m.stages as any[];
    expect(s0.steps[0].description).toBe('Fetches the repository at the pushed commit.');
    expect(s1.steps.map((s: any) => s.name)).toEqual(['build', 'test']);
    expect(s1.steps[1].description).toBe('Runs the unit tests.');
  });
  test('resolved model matches and explicit mappings', () => {
    const [s0, s1, s2] = m.stages as any[];
    expect(s0.steps[0].input).toEqual({
      repo: { ref: 'input.repo' },
      sha: { ref: 'input.sha' },
    });
    expect(s1.steps.map((s: any) => [s.name, s.input])).toEqual([
      ['build', { source: { ref: 'checkout' } }],
      ['test', { source: { ref: 'checkout' } }],
    ]);
    expect(s2.steps[0].input).toEqual({ artifact: { ref: 'build' } });
  });
  test('when blocks, optional inputs, failure handlers, triggers', () => {
    const w = (m.stages as any[])[3];
    expect(w.when).toEqual({ deployTo: ['prod'] });
    expect(w.stages[1].when).toEqual({ publishCli: [true] });
    const summary = (m.stages as any[])[4].steps[0];
    expect(summary.inputTypes.prod).toEqual({ model: 'ProdDeployment', optional: true });
    expect(summary.output).toBeNull();
    expect(m.failureHandlers[0]!.step.input).toEqual({ failure: { ref: 'failure' } });
    expect(m.failureHandlers[0]!.steps).toEqual(['deploy-prod', 'deploy-beta']);
    expect(m.triggers.map((t) => t.name)).toEqual(['push', 'poll', 'release']);
    expect(m.triggers[2]).toEqual({
      kind: 'webhook',
      name: 'release',
      verify: {
        scheme: 'hmac-sha256',
        secret: 'RELEASE_HOOK_SECRET',
        header: 'X-Hub-Signature-256',
        prefix: 'sha256=',
      },
    });
  });
  test('models: fileRef marker, strict output view, input view only where it differs', () => {
    const art = m.models.BuildArtifact! as any;
    expect(art.output.properties.image['x-olve-kind']).toBe('fileRef');
    expect(art.output.additionalProperties).toBe(false);
    expect(art.output.properties.image.additionalProperties).toBe(false);
    expect(art.input).toBeUndefined();
    const req = m.models.DeployRequest! as any;
    expect(req.input.required).toEqual(['sha']);
    expect(req.output.required).toEqual(['repo', 'sha', 'deployTo', 'publishCli']);
    expect(JSON.stringify(m)).not.toContain('$schema');
  });
  test('the manifest holds no paths', () => {
    expect(JSON.stringify(m)).not.toMatch(/scripts\/|\$bunfs|\/home\//);
  });
  test('secrets sorted, harness included', () => {
    expect(m.secrets.map((s) => s.name)).toEqual([
      'GITHUB_TOKEN',
      'RELEASE_HOOK_SECRET',
      'SSH_PRIVATE_KEY',
    ]);
    expect(m.harness).toEqual(cfg.harness);
  });
});

describe('manifest.schema.json', () => {
  test('is up to date with src/describe/manifest-schema.ts (bun run gen-schema)', () => {
    expect(readFileSync(join(SDK, 'manifest.schema.json'), 'utf8')).toBe(
      canonicalJson(manifestJsonSchema()),
    );
  });
});

describe('describe-time checks', () => {
  test('valid pipeline has no problems', () =>
    expect(problems(base().stage(make).stage(use))).toEqual([]));
  test('step without a description', () => {
    const s = step({ name: 's', image: 'alpine' } as never);
    expectProblem(base().stage(s), /description must be a string/, ['s']);
  });
  test('reusing an extended pipeline value is a problem; chaining is not', () => {
    const p = base().stage(make);
    p.stage(use);
    expectProblem(p.stage(use), /extended twice/);
    expect(
      problems(
        base()
          .stage(make)
          .when({ env: 'prod' }, (q) => q.stage(use)),
      ),
    ).toEqual([]);
  });
  test('custom step without handler', () =>
    expectProblem(base().stage(make), /step 'make' has no implementation/, []));
  test('handler file without step / for a step that carries its implementation', () => {
    expectProblem(
      base().stage(make),
      /implements step 'ghost', which is not in the pipeline/,
      ['make', 'ghost'],
    );
    const lib = implement(
      step({ name: 's', description: '', kind: 'lib', image: 'alpine' }),
      () => {},
    );
    expectProblem(
      base().stage(lib),
      /implements 's', a lib step that carries its own implementation/,
      ['s'],
    );
  });
  test('two schemas under one model name', () => {
    const Art2 = model('Art', z.object({ other: z.number() }));
    const s = step({ name: 's', description: '', image: 'alpine', output: Art2 });
    expectProblem(
      base()
        .stage(make)
        .stage(s as any),
      /two different schemas .* 'Art'/,
    );
  });
  test('identical re-registration of a model name is fine', () => {
    const Same = model('Dep', z.object({ url: z.string() }));
    const s = step({
      name: 's',
      description: '',
      image: 'alpine',
      input: { art: Art },
      output: Same,
    });
    expect(
      problems(
        base()
          .stage(make)
          .stage(use)
          .stage([s, (f: any) => ({ art: f.make })] as any),
      ),
    ).toEqual([]);
  });
  test('not available / same-stage sibling', () => {
    expectProblem(base().stage(use as any), /Art \(input 'art'\) is not available/);
    expectProblem(
      (base() as any).stage(make, use),
      /Art \(input 'art'\) is not available/,
    );
  });
  test('ambiguous producers', () =>
    expectProblem(
      (base() as any).stage(make, make2).stage(use),
      /several producers \(make, make2\)/,
    ));
  test('maybe-available as required input, optional ok', () => {
    expectProblem(
      (base() as any).when({ env: 'prod' }, (p: any) => p.stage(make)).stage(use),
      /only maybe-available/,
    );
    const opt = step({
      name: 'other',
      description: '',
      image: 'alpine',
      input: { art: optional(Art) },
    });
    expect(
      problems(
        (base() as any).when({ env: 'prod' }, (p: any) => p.stage(make)).stage(opt),
      ),
    ).toEqual([]);
  });
  test('optional() still needs a producer', () => {
    const opt = step({
      name: 'other',
      description: '',
      image: 'alpine',
      input: { art: optional(Art) },
    });
    expectProblem(
      base().stage(opt as any),
      /not available .*optional\(\) means maybe-skipped/,
    );
  });
  test('plain schema input needs a mapper', () => {
    const s = step({
      name: 's',
      description: '',
      image: 'alpine',
      input: { tag: z.string() },
    });
    expectProblem(base().stage(s as any), /plain schema, not a model/);
  });
  test('mapper: logic, unknown field, missing/extra keys, unavailable root', () => {
    const s = step({
      name: 's',
      description: '',
      image: 'alpine',
      input: { tag: z.string() },
    });
    expectProblem(
      base().stage([s, (f: any) => ({ tag: f.input.flag ? 'a' : 'b' })] as any),
      /ternary/,
    );
    expectProblem(
      base().stage([s, (f: any) => ({ tag: f.input.shaa })] as any),
      /model Req has no field 'shaa'/,
    );
    expectProblem(base().stage([s, () => ({})] as any), /does not provide input 'tag'/);
    expectProblem(
      base().stage([s, (f: any) => ({ tag: f.input.sha, extra: 1 })] as any),
      /'extra', which is not an input/,
    );
    expectProblem(
      base().stage([s, (f: any) => ({ tag: f.make.version })] as any),
      /'make', which is not available/,
    );
    expectProblem(
      base()
        .stage(make)
        .stage([s, (f: any) => ({ tag: f.make.nested.b })] as any),
      /has no field 'nested.b'/,
    );
    expect(
      problems(
        base()
          .stage(make)
          .stage([s, (f: any) => ({ tag: f.make.nested.a })] as any),
      ),
    ).toEqual([]);
  });
  test('bare .stage(step, mapper) form is rejected at runtime too', () =>
    expectProblem(
      (base() as any).stage(make).stage(use, (f: any) => ({ art: f.make })),
      /bare function/,
    ));
  test('when: unknown field, non-literal field, invalid value', () => {
    expectProblem(
      base().when({ nope: 'x' } as any, (p) => p),
      /'nope' is not a field/,
    );
    expectProblem(
      base().when({ sha: 'x' } as any, (p) => p),
      /'sha' is not a literal-like field/,
    );
    expectProblem(
      base().when({ n: 1 } as any, (p) => p),
      /'n' is not a literal-like field/,
    );
    expectProblem(
      base().when({ env: 'staging' } as any, (p) => p),
      /"staging" is not a valid value of 'env'/,
    );
    expect(
      problems(base().when({ env: ['beta', 'prod'], flag: true }, (p) => p)),
    ).toEqual([]);
  });
  test('duplicate and reserved step names, name format', () => {
    expectProblem((base() as any).stage(make).stage(make), /duplicate step name 'make'/);
    const input = step({ name: 'input', description: '', image: 'alpine' });
    expectProblem(base().stage(input as any), /'input' is reserved/);
    const failure = step({ name: 'failure', description: '', image: 'alpine' });
    expectProblem(base().stage(failure as any), /'failure' is reserved/);
    const bad = step({ name: 'Bad_Name', description: '', image: 'alpine' });
    expectProblem(base().stage(bad as any), /must be lower-case kebab/, ['Bad_Name']);
  });
  test('triggers: one push, unique polls, header secrets', () => {
    const T = secret('TOKEN', 't');
    expectProblem(
      (base() as any).onPush(() => ({})).onPush(() => ({})),
      /duplicate trigger 'push'/,
    );
    expectProblem(
      base()
        .onPoll({ url: 'u', valuePath: 'sha', intervalSeconds: 60 }, (v) => ({
          sha: v,
          n: 1,
        }))
        .onPoll({ url: 'u2', valuePath: 'sha', intervalSeconds: 60 }, (v) => ({
          sha: v,
          n: 1,
        })),
      /duplicate trigger 'poll'/,
    );
    expectProblem(
      base().onPoll(
        {
          url: 'u',
          secrets: [T],
          headers: { A: '$SECRET:OTHER' } as any,
          valuePath: 'sha',
          intervalSeconds: 60,
        },
        (v) => ({ sha: v, n: 1 }),
      ),
      /references secret 'OTHER'/,
    );
    expectProblem(
      base().onWebhook({ name: 'Bad', verify: bearer(T) }, () => null),
      /webhook trigger name 'Bad' must be lower-case kebab/,
    );
    expectProblem(
      base().onWebhook(
        { name: 'h', verify: hmacSha256({ secret: T, header: '' }) },
        () => null,
      ),
      /signature header must not be empty/,
    );
    expectProblem(
      base()
        .onPoll({ name: 'x', url: 'u', valuePath: 'sha', intervalSeconds: 60 }, (v) => ({
          sha: v,
          n: 1,
        }))
        .onWebhook({ name: 'x', verify: bearer(T) }, () => null),
      /duplicate trigger 'x'/,
    );
  });
  test('secrets: conflicting descriptions, name format', () => {
    const a = step({
      name: 's',
      description: '',
      image: 'alpine',
      secrets: [secret('X', 'one')],
    });
    const b = step({
      name: 't',
      description: '',
      image: 'alpine',
      secrets: [secret('X', 'two')],
    });
    expectProblem((base() as any).stage(a, b), /secret 'X' is declared twice/);
    const c = step({
      name: 's',
      description: '',
      image: 'alpine',
      secrets: [secret('lower', 'x')],
    });
    expectProblem(base().stage(c), /secret name 'lower'/);
  });
  test('onFailure: inputs must be FailureContext, scope must exist', () => {
    expectProblem(
      base()
        .stage(make)
        .onFailure(use as any),
      /must all be FailureContext/,
    );
    const fh = step({ name: 'fh', description: '', image: 'alpine' });
    const ghost = step({ name: 'ghost', description: '', image: 'alpine' });
    expectProblem(
      base()
        .stage(make)
        .onFailure(fh, { steps: [ghost as any] }),
      /'ghost' is not a step of this pipeline/,
    );
  });
  test('non-JSON-representable model', () => {
    const D = model('D', z.object({ at: z.date() }));
    const s = step({ name: 's', description: '', image: 'alpine', output: D });
    expectProblem(base().stage(s), /not representable as JSON Schema/);
  });
  test('all problems are reported together', () => {
    const s = step({
      name: 's',
      description: '',
      image: 'alpine',
      input: { tag: z.string() },
    });
    expect(
      problems((base() as any).stage(use).stage(s), []).length,
    ).toBeGreaterThanOrEqual(4);
  });
});

test('Source library model is a dir fileRef carrier', () => {
  expect(Source.name).toBe('Source');
});

describe('retention', () => {
  const manifestOf = (p: unknown) =>
    describePipeline(p, { handlerNames: ['make'], harness });
  test('omitted unless set; set fields are recorded as given', () => {
    expect('retention' in manifestOf(base().stage(make))).toBe(false);
    expect(
      'retention' in
        manifestOf(pipeline({ name: 't', input: Req, retention: {} }).stage(make)),
    ).toBe(false);
    const m = manifestOf(
      pipeline({
        name: 't',
        input: Req,
        retention: { keepLatestRuns: 5, minRunAgeDays: 30 },
      }).stage(make),
    );
    expect(m.retention).toEqual({ keepLatestRuns: 5, minRunAgeDays: 30 });
    expect(Manifest.safeParse(m).success).toBe(true);
  });
  test('fields must be non-negative integers', () => {
    expectProblem(
      pipeline({ name: 't', input: Req, retention: { keepLatestRuns: -1 } }).stage(make),
      /retention\.keepLatestRuns must be a non-negative integer/,
    );
    expectProblem(
      pipeline({ name: 't', input: Req, retention: { minRunAgeDays: 1.5 } }).stage(make),
      /retention\.minRunAgeDays must be a non-negative integer/,
    );
    expectProblem(
      pipeline({ name: 't', input: Req, retention: { buildGraceDays: NaN } }).stage(make),
      /retention\.buildGraceDays must be a non-negative integer/,
    );
  });
});
