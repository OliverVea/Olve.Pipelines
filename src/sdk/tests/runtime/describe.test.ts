// Describe-time checks and manifest shape. Pipelines are built in-test; the type errors they
// contain are deliberate (describe must catch what TypeScript was told to ignore).
/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  bearer,
  callback,
  config,
  external,
  FailureContext,
  hmacSha256,
  model,
  optional,
  person,
  pipeline,
  poll,
  secret,
  secretRef,
  step,
  z,
  Source,
} from '../../src/index.ts';
import { describePipeline, DescribeError } from '../../src/describe/describe.ts';
import { Manifest, manifestJsonSchema } from '../../src/describe/manifest-schema.ts';
import { canonicalJson } from '../../src/util/canonical.ts';
import { devConfig } from '../../src/dev/cli.ts';
import { EXAMPLE, SDK, stubStep } from './helpers.ts';

const harness = { bun: '1.4.3', sdk: '0.0.0', sdkHash: 'sha256:' + 'a'.repeat(64) };
const Req = model(
  'Req',
  z.object({
    sha: z.string(),
    env: z.enum(['beta', 'prod']).default('beta'),
    flag: z.boolean().default(false),
    n: z.number(),
    lit: z.literal('app').default('app'),
    u: z.union([z.literal('a'), z.literal('b')]).default('a'),
    m: z.union([z.literal('a'), z.string()]).default('a'),
  }),
);
const Art = model(
  'Art',
  z.object({
    version: z.string(),
    nested: z.object({ a: z.string() }),
    opt: z.object({ a: z.string() }).nullable(),
    du: z.discriminatedUnion('k', [
      z.object({ k: z.literal('a'), x: z.string() }),
      z.object({ k: z.literal('b') }),
    ]),
    both: z.intersection(z.object({ x: z.string() }), z.record(z.string(), z.string())),
    labels: z.record(z.string(), z.string()),
    any: z.unknown().meta({ title: 'T', description: 'D' }).default(null),
    raw: z.unknown(),
  }),
);
const Dep = model('Dep', z.object({ url: z.string() }));
const N = model('N', z.object({ n: z.string() }));
const Bad = model('bad-name', z.object({}));
const make = stubStep({
  name: 'make',
  description: '',
  image: 'alpine',
  input: { req: Req },
  output: Art,
});
const make2 = stubStep({
  name: 'make2',
  description: '',
  image: 'alpine',
  input: { req: Req },
  output: Art,
});
const use = stubStep({
  name: 'use',
  description: '',
  image: 'alpine',
  input: { art: Art },
  output: Dep,
});
/** An untyped stub step with an empty description and the alpine image. */
const s = (def: any) =>
  stubStep({ description: '', image: 'alpine', ...def } as never) as any;
const plain = s({ name: 'p', input: { tag: z.string() } });
const opt = s({ name: 'o', input: { art: optional(Art) } });
const fh = s({ name: 'fh', input: { ctx: FailureContext } });

const T = secret('TOKEN', 't');
const S = secret('S_KEY', 's');
const X = config(
  'x',
  'X.',
  z.object({
    prod: z.discriminatedUnion('mode', [
      z.object({ mode: z.literal('a'), key: secretRef(S), ns: z.string() }),
      z.object({ mode: z.literal('b'), ns: z.string() }),
    ]),
    labels: z.record(z.string(), z.string()),
    nul: z.string().nullable().default(null),
  }),
);
const K = config('k', 'K.', z.object({ keys: z.record(z.string(), secretRef(S)) }));
const PUB = config('pub', 'P.', z.object({ repo: z.string(), key: secretRef(S) }));
const TGT = config('tgt', 'T.', z.object({ host: z.string() }));
const NAME = config('name', 'N.', z.string());
const BadCfg = config(
  'Bad',
  'B.',
  z.discriminatedUnion('m', [z.object({ m: z.literal('a') })]),
);
/** X.prod narrowed to case a, for use outside that case. */
const leaked = (() => {
  let r: any;
  (pipeline({ name: 't', input: Req }) as any).select(X.prod, {
    a: (q: any, ref: any) => ((r = ref), q),
    b: (q: any) => q,
  });
  return r;
})();
const errorOf = (fn: () => unknown) => {
  try {
    fn();
  } catch (e) {
    return (e as Error).message;
  }
  throw new Error('did not throw');
};
const dateRefusal = errorOf(() => z.toJSONSchema(z.date()));
const undef: any = undefined;
const undefRead = errorOf(() => undef.x);

function problems(p: unknown): string[] {
  try {
    describePipeline(p, { harness });
  } catch (e) {
    if (e instanceof DescribeError) return [...e.problems];
    throw e;
  }
  return [];
}
const base = () => pipeline({ name: 't', input: Req });
const anyBase = () => base() as any;
const stepsOf = (m: any): any[] =>
  m.stages.flatMap((x: any) => ('steps' in x ? x.steps : []));

describe('example manifest', async () => {
  const cfg = await devConfig(EXAMPLE, true);
  const m = describePipeline(cfg.pipeline, {
    codeHash: cfg.codeHash,
    harness: cfg.harness,
  });

  test('validates against the manifest schema', () =>
    expect(Manifest.safeParse(m).success).toBe(true));
  test('is deterministic', () => {
    const again = describePipeline(cfg.pipeline, {
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

describe('manifest', () => {
  const manifestOf = (p: unknown) => describePipeline(p, { harness }) as any;
  test('step descriptors', () => {
    const B = secret('B_KEY', 'b');
    const A = secret('A_KEY', 'a');
    const tuned = s({
      name: 'tuned',
      input: { req: Req },
      timeoutSeconds: 600,
      retries: 2,
      secrets: [B, A],
    });
    const once = s({ name: 'once', input: { req: Req }, retries: 0 });
    const consume = s({ name: 'consume', input: { art: Art } });
    const m = manifestOf(
      anyBase()
        .stage(make, tuned, once)
        .stage(consume, [plain, (f: any) => ({ tag: f.input.sha })]),
    );
    const [, t, o, c, p] = stepsOf(m);
    expect(t).toMatchObject({
      timeoutSeconds: 600,
      retries: 2,
      secrets: ['A_KEY', 'B_KEY'],
    });
    expect(o.retries).toBe(0);
    expect('timeoutSeconds' in o).toBe(false);
    expect(c).toEqual({
      name: 'consume',
      description: '',
      kind: 'custom',
      image: 'alpine',
      inputTypes: { art: { model: 'Art', optional: false } },
      input: { art: { ref: 'make' } },
      output: null,
      secrets: [],
      config: {},
    });
    expect('retries' in p).toBe(false);
    expect(p.inputTypes.tag).toEqual({ schema: { output: { type: 'string' } } });
  });
  test('triggers', () => {
    const U = secret('A_TOKEN', 'a');
    const m = manifestOf(
      anyBase()
        .stage(make)
        .onPoll(
          {
            url: 'https://x',
            secrets: [T, U],
            headers: { Authorization: 'Bearer $SECRET:TOKEN' },
            valuePath: 'sha',
            intervalSeconds: 60,
          },
          (v: string) => ({ sha: v, n: 1 }),
        )
        .onWebhook({ name: 'hook', verify: bearer(T) }, () => null),
    );
    expect(m.triggers).toEqual([
      {
        kind: 'poll',
        name: 'poll',
        url: 'https://x',
        headers: { Authorization: 'Bearer $SECRET:TOKEN' },
        secrets: ['A_TOKEN', 'TOKEN'],
        valuePath: 'sha',
        intervalSeconds: 60,
      },
      { kind: 'webhook', name: 'hook', verify: { scheme: 'bearer', secret: 'TOKEN' } },
    ]);
  });
  test('failure handlers: one handler in several scopes, unscoped', () => {
    const scoped = manifestOf(
      anyBase()
        .stage(make)
        .stage(use)
        .onFailure(fh, { steps: [make] })
        .onFailure(fh, { steps: [use] }),
    );
    expect(scoped.failureHandlers.map((h: any) => h.steps)).toEqual([['make'], ['use']]);
    const unscoped = manifestOf(base().stage(make).onFailure(fh));
    expect(unscoped.failureHandlers[0].steps).toBeNull();
  });
  // The C# FailureContextBuilder tests validate against this fixture's schema.
  test('FailureContext matches the backend test fixture', () => {
    const fixture = JSON.parse(
      readFileSync(
        join(
          SDK,
          '../backend/Olve.Pipelines.UnitTests/Manifests/Fixtures/test-fixture.manifest.json',
        ),
        'utf8',
      ),
    );
    const m = manifestOf(base().stage(make).onFailure(fh));
    expect(m.models.FailureContext).toEqual(fixture.models.FailureContext);
  });
  test('a model has an input view only where it differs beyond strictness', () => {
    const R = model(
      'R',
      z.object({ tags: z.record(z.string(), z.string()), off: z.literal(false) }),
    );
    const m = manifestOf(base().stage(s({ name: 'r', output: R })));
    expect(m.models.R.input).toBeUndefined();
  });
});

describe('describe-time checks', () => {
  const accepted: [string, () => unknown][] = [
    ['a valid pipeline', () => base().stage(make).stage(use)],
    [
      'chaining after a when block',
      () =>
        base()
          .stage(make)
          .when({ env: 'prod' }, (q) => q.stage(use)),
    ],
    [
      'an identical re-registration of a model name',
      () => {
        const Same = model('Dep', z.object({ url: z.string() }));
        const r = s({ name: 'r', input: { art: Art }, output: Same });
        return anyBase()
          .stage(make)
          .stage(use)
          .stage([r, (f: any) => ({ art: f.make })]);
      },
    ],
    [
      'an optional input of a maybe-available output',
      () =>
        anyBase()
          .when({ env: 'prod' }, (p: any) => p.stage(make))
          .stage(opt),
    ],
    [
      'a maybe-available output mapped whole into an optional input',
      () =>
        anyBase()
          .when({ env: 'prod' }, (p: any) => p.stage(make))
          .stage([opt, (f: any) => ({ art: f.make })]),
    ],
    ...(
      [
        ['a nested field', (f: any) => ({ tag: f.make.nested.a })],
        ['a nullable field', (f: any) => ({ tag: f.make.opt.a })],
        ['a discriminated union', (f: any) => ({ tag: f.make.du.x })],
        ['an intersection', (f: any) => ({ tag: f.make.both.x })],
        ['a record', (f: any) => ({ tag: f.make.labels.app })],
        [
          'an unknown with title, description and default',
          (f: any) => ({ tag: f.make.any.deep }),
        ],
        ['an unknown', (f: any) => ({ tag: f.make.raw.deep })],
      ] as const
    ).map(([name, mapper]): [string, () => unknown] => [
      `a mapper ref through ${name}`,
      () => anyBase().stage(make).stage([plain, mapper]),
    ]),
    [
      'when on enum, boolean, literal and literal-union fields',
      () =>
        base().when(
          { env: ['beta', 'prod'], flag: false, lit: 'app', u: ['a', 'b'] },
          (p) => p.stage(make),
        ),
    ],
    [
      'a failure handler on a same-named FailureContext copy',
      () =>
        base()
          .stage(make)
          .onFailure(
            s({
              name: 'fh',
              input: { ctx: model('FailureContext', FailureContext.schema) },
            }),
          ),
    ],
    [
      'a poll trigger without secrets',
      () =>
        base()
          .stage(make)
          .onPoll({ url: 'u', valuePath: 'sha', intervalSeconds: 60 }, (v) => ({
            sha: v,
            n: 1,
          })),
    ],
    [
      'external inputs: a dotted key, mixed sources, an http poll every 10 s',
      () => {
        const V = model('Verdict', z.object({ ok: z.boolean() }));
        const every10 = poll({
          url: 'http://in-cluster/s',
          valuePath: 'v',
          intervalSeconds: 10,
        });
        return base().stage(
          s({
            name: 'ok',
            input: {
              'x.y': external(V, { title: '?' }),
              mixed: external(V, { title: '?', from: [person(), callback()] }),
              h: external(V, { title: '?', from: [every10] }),
            },
          }),
        );
      },
    ],
    [
      'a no-output step in both select cases',
      () =>
        anyBase().select(X.prod, {
          a: (q: any) => q.stage(s({ name: 'n1' })),
          b: (q: any) => q.stage(s({ name: 'n1' })),
        }),
    ],
    [
      'narrowed refs need only their branch secrets; arrays and null settings',
      () =>
        anyBase().select(X.prod, {
          a: (q: any, r: any) =>
            q.stage(s({ name: 'da', settings: { t: r }, secrets: [S] })),
          b: (q: any, r: any) =>
            q.stage(s({ name: 'db', settings: { t: r, l: [X.labels.app], n: null } })),
        }),
    ],
  ];
  test.each(accepted)('accepts %s', (_name, p) => expect(problems(p())).toEqual([]));

  const reusedValue = () => {
    const p = base().stage(make);
    p.stage(use);
    return p.stage(use);
  };
  const notLiteral = (f: string) =>
    `when: '${f}' is not a literal-like field (enum, boolean or literal)`;
  const modelName = "model name 'bad-name' must match /^[A-Za-z][A-Za-z0-9_]*$/";
  const kebab = '/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/';
  const maybeMapped = (at: string, ref: string) =>
    `${at}: '${ref}' is only maybe-available (produced inside a when block); map it whole into an optional(...) input`;
  const whenReturn =
    'a when block must return the pipeline it was given (p => p.stage(...))';
  const pollCfg = { url: 'u', valuePath: 'sha', intervalSeconds: 60 };
  const toInput = (v: string) => ({ sha: v, n: 1 });
  const V = model('Verdict', z.object({ ok: z.boolean() }));

  const refused: [string, () => unknown, unknown][] = [
    // models, inputs, outputs
    [
      'a pipeline input model name',
      () => pipeline({ name: 't', input: Bad }),
      [`pipeline input: ${modelName}`],
    ],
    [
      'an output model name',
      () => base().stage(s({ name: 's', output: Bad })),
      [`step 's' output: ${modelName}`],
    ],
    [
      'an input model name',
      () => base().stage(s({ name: 's', input: { b: Bad, c: optional(Bad) } })),
      [
        `step 's' input 'b': ${modelName}`,
        `step 's' input 'c': ${modelName}`,
        "step 's': bad-name (input 'b') is not available at this stage",
        "step 's': bad-name (input 'c') is not available at this stage (optional() means maybe-skipped; there must still be an earlier producer)",
      ],
    ],
    [
      'an external input model name',
      () => base().stage(s({ name: 's', input: { e: external(Bad, { title: '?' }) } })),
      [`step 's' external input 'e': ${modelName}`],
    ],
    [
      'two schemas under one model name',
      () =>
        anyBase()
          .stage(make)
          .stage(s({ name: 's', output: model('Art', z.object({ other: z.number() })) })),
      [
        "two different schemas are registered under the model name 'Art' (one schema per model name)",
      ],
    ],
    [
      'a model that is not representable',
      () =>
        base().stage(s({ name: 's', output: model('D', z.object({ at: z.date() })) })),
      [`step 's' output: model 'D' is not representable as JSON Schema (${dateRefusal})`],
    ],
    [
      'a plain schema that is not representable',
      () =>
        anyBase().stage([
          s({ name: 's', input: { at: z.date() } }),
          (f: any) => ({ at: f.input.sha }),
        ]),
      [
        `step 's' input 'at': schema is not representable as JSON Schema (${dateRefusal})`,
      ],
    ],
    [
      'an input that is not a model or schema',
      () => base().stage(s({ name: 's', input: { x: 42 } })),
      [
        "step 's' input 'x': must be a model, optional(model) or a zod schema",
        "step 's': input 'x' is a plain schema, not a model, so it cannot be matched by model; add a mapper ([step, from => ({ ... })])",
      ],
    ],
    [
      'an output that is not a model',
      () => base().stage(s({ name: 's', output: z.object({}) })),
      expect.arrayContaining(["step 's': output must be a model"]),
    ],

    // step fields
    [
      'a step without a description',
      () => base().stage(stubStep({ name: 's', image: 'alpine' } as never)),
      ["step 's': description must be a string (it may be empty)"],
    ],
    [
      'a staged step without an implementation',
      () => base().stage(step({ name: 'bare', description: '', image: 'alpine' })),
      ["step 'bare' has no implementation (stage implement(step({...}), fn))"],
    ],
    [
      'a failure handler without an implementation',
      () =>
        base()
          .stage(make)
          .onFailure(step({ name: 'bare', description: '', image: 'alpine' })),
      ["step 'bare' has no implementation (stage implement(step({...}), fn))"],
    ],
    [
      'a blank image',
      () => base().stage(s({ name: 's', image: ' ' })),
      ["step 's': image must be a non-empty string"],
    ],
    [
      'no image',
      () => base().stage(s({ name: 's', image: undefined })),
      ["step 's': image must be a non-empty string"],
    ],
    [
      'an empty kind',
      () => base().stage(s({ name: 's', kind: '' })),
      ["step 's': kind must be a non-empty string"],
    ],
    [
      'a non-string kind',
      () => base().stage(s({ name: 's', kind: 42 })),
      ["step 's': kind must be a non-empty string"],
    ],
    [
      'timeoutSeconds 0',
      () => base().stage(s({ name: 's', timeoutSeconds: 0 })),
      ["step 's': timeoutSeconds must be a positive integer"],
    ],
    [
      'retries -1',
      () => base().stage(s({ name: 's', retries: -1 })),
      ["step 's': retries must be a non-negative integer"],
    ],
    [
      "a literal '$' settings key",
      () => base().stage(s({ name: 's', settings: { a: { $config: 'x' } } })),
      [
        "step 's': invalid custom settings (key '$config': keys starting with '$' are reserved)",
      ],
    ],

    // step names
    [
      'a duplicate step name',
      () => anyBase().stage(make).stage(make),
      ["duplicate step name 'make'"],
    ],
    [
      'the reserved name input',
      () => base().stage(s({ name: 'input' })),
      ["stage: 'input' is reserved and cannot be a step name"],
    ],
    [
      'the reserved name failure',
      () => base().stage(s({ name: 'failure' })),
      ["stage: 'failure' is reserved and cannot be a step name"],
    ],
    [
      'a step name that is not kebab',
      () => base().stage(s({ name: 'Bad_Name' })),
      [`stage: step name 'Bad_Name' must be lower-case kebab (${kebab})`],
    ],
    [
      'a numeric step name',
      () => base().stage(s({ name: 42 })),
      [`stage: step name '42' must be lower-case kebab (${kebab})`],
    ],

    // secrets
    [
      'a secret declared with two descriptions',
      () =>
        anyBase().stage(
          s({ name: 's', secrets: [secret('X', 'one')] }),
          s({ name: 't', secrets: [secret('X', 'two')] }),
        ),
      ["secret 'X' is declared twice with different descriptions"],
    ],
    [
      'a secret name',
      () => base().stage(s({ name: 's', secrets: [secret('lower', 'x')] })),
      ["step 's': secret name 'lower' must match /^[A-Z_][A-Z0-9_]*$/"],
    ],

    // builder misuse
    [
      'reusing an extended pipeline value',
      reusedValue,
      [
        'a pipeline value was extended twice: chain the calls (pipeline({...}).stage(a).stage(b)) instead of reusing an earlier value',
        "duplicate step name 'use'",
      ],
    ],
    [
      'a bare (step, mapper) stage',
      () =>
        anyBase()
          .stage(make)
          .stage(use, (f: any) => ({ art: f.make })),
      [
        'stage entry 2 is a bare function; mappers are given as [step, from => ({ ... })] tuples',
      ],
    ],
    [
      'stage entries that are not steps or pairs',
      () =>
        anyBase()
          .stage(make, 42)
          .stage([make2, 'x'])
          .stage([{}, (f: any) => f])
          .stage([make2, (f: any) => f, 1]),
      [2, 1, 1, 1].map(
        (i) => `stage entry ${i} is neither a step nor a [step, mapper] pair`,
      ),
    ],
    ['an empty stage', () => anyBase().stage(), ['empty stage']],
    [
      'a when block that returns anything but its pipeline',
      () =>
        anyBase()
          .when({ env: 'prod' }, () => undefined)
          .when({ env: 'prod' }, () => null)
          .when({ env: 'prod' }, () => ({}))
          .when({ env: 'prod' }, () => base()),
      [whenReturn, whenReturn, whenReturn, whenReturn],
    ],
    [
      'all problems at once',
      () =>
        anyBase()
          .stage(
            step({ name: 'bare', description: '', image: 'alpine', input: { art: Art } }),
          )
          .stage(
            step({
              name: 's',
              description: '',
              image: 'alpine',
              input: { tag: z.string() },
            }),
          ),
      [
        "step 'bare': Art (input 'art') is not available at this stage",
        "step 'bare' has no implementation (stage implement(step({...}), fn))",
        "step 's': input 'tag' is a plain schema, not a model, so it cannot be matched by model; add a mapper ([step, from => ({ ... })])",
        "step 's' has no implementation (stage implement(step({...}), fn))",
      ],
    ],

    // inputs by model
    [
      'a model that is not available',
      () => anyBase().stage(use),
      ["step 'use': Art (input 'art') is not available at this stage"],
    ],
    [
      'a model from a same-stage sibling',
      () => anyBase().stage(make, use),
      ["step 'use': Art (input 'art') is not available at this stage"],
    ],
    [
      'several producers, listed sorted',
      () => anyBase().stage(make2, make).stage(use),
      [
        "step 'use': Art (input 'art') has several producers (make, make2); use a mapper to pick one",
      ],
    ],
    [
      'a maybe-available model as a required input',
      () =>
        anyBase()
          .when({ env: 'prod' }, (p: any) => p.stage(make))
          .stage(use),
      [
        "step 'use': Art (input 'art') is only maybe-available (produced inside a when block); declare it optional(Art)",
      ],
    ],
    [
      'optional() without any producer',
      () => anyBase().stage(opt),
      [
        "step 'o': Art (input 'art') is not available at this stage (optional() means maybe-skipped; there must still be an earlier producer)",
      ],
    ],
    [
      'a plain schema input without a mapper',
      () => anyBase().stage(plain),
      [
        "step 'p': input 'tag' is a plain schema, not a model, so it cannot be matched by model; add a mapper ([step, from => ({ ... })])",
      ],
    ],

    // mappers
    [
      'mapper logic',
      () => anyBase().stage([plain, (f: any) => ({ tag: f.input.flag ? 'a' : 'b' })]),
      [
        "step 'p': mapper uses a ternary (`? :`); mappers may only contain property access and literals — move logic into the handler or a when block",
      ],
    ],
    [
      'a mapper ref to an unknown field',
      () => anyBase().stage([plain, (f: any) => ({ tag: f.input.shaa })]),
      ["step 'p' input 'tag': 'input.shaa' — model Req has no field 'shaa'"],
    ],
    [
      'a mapper ref past a string field',
      () =>
        anyBase()
          .stage(make)
          .stage([plain, (f: any) => ({ tag: f.make.version.deep })]),
      [
        "step 'p' input 'tag': 'make.version.deep' — model Art has no field 'version.deep'",
      ],
    ],
    [
      'a nested mapping field that does not exist',
      () =>
        anyBase()
          .stage(make)
          .stage([
            s({ name: 'q', input: { o: z.object({ a: z.string() }) } }),
            (f: any) => ({ o: { a: f.make.nested.b } }),
          ]),
      ["step 'q' input 'o'.a: 'make.nested.b' — model Art has no field 'nested.b'"],
    ],
    [
      'a mapper that misses an input',
      () => anyBase().stage([plain, () => ({})]),
      ["step 'p': mapper does not provide input 'tag'"],
    ],
    [
      'a mapper that provides an extra key',
      () => anyBase().stage([plain, (f: any) => ({ tag: f.input.sha, extra: 1 })]),
      ["step 'p': mapper provides 'extra', which is not an input of the step"],
    ],
    [
      'a mapper ref to a step that has not run',
      () => anyBase().stage([plain, (f: any) => ({ tag: f.make.version })]),
      [
        "step 'p': mapper references 'make', which is not available at this stage (available: input)",
      ],
    ],
    [
      'a mapper ref to a step without output',
      () =>
        anyBase()
          .stage(s({ name: 'gate' }))
          .stage([plain, (f: any) => ({ tag: f.gate })]),
      [
        "step 'p': mapper references 'gate', which is not available at this stage (available: input)",
      ],
    ],
    [
      'a mapper that throws',
      () => anyBase().stage([plain, () => ({ tag: undef.x })]),
      [`step 'p': mapper threw: ${undefRead}`],
    ],
    [
      'a field of a maybe-available output',
      () =>
        anyBase()
          .when({ env: 'prod' }, (p: any) => p.stage(make))
          .stage([plain, (f: any) => ({ tag: f.make.version })]),
      [maybeMapped("step 'p' input 'tag'", 'make.version')],
    ],
    [
      'a maybe-available output inside a nested mapping',
      () =>
        anyBase()
          .when({ env: 'prod' }, (p: any) => p.stage(make))
          .stage([opt, (f: any) => ({ art: { version: f.make.version } })]),
      [maybeMapped("step 'o' input 'art'.version", 'make.version')],
    ],

    // when
    [
      'when on an unknown field',
      () => anyBase().when({ nope: 'x' }, (p: any) => p),
      ["when: 'nope' is not a field of the pipeline input Req"],
    ],
    [
      'when on a string field',
      () => anyBase().when({ sha: 'x' }, (p: any) => p),
      [notLiteral('sha')],
    ],
    [
      'when on a number field',
      () => anyBase().when({ n: 1 }, (p: any) => p),
      [notLiteral('n')],
    ],
    [
      'when on a union with a non-literal branch',
      () => anyBase().when({ m: 'a' }, (p: any) => p),
      [notLiteral('m')],
    ],
    [
      'when with an invalid enum value',
      () => anyBase().when({ env: 'staging' }, (p: any) => p),
      [`when: "staging" is not a valid value of 'env' ("beta", "prod")`],
    ],
    [
      'when with an invalid literal-union value',
      () => anyBase().when({ u: 'c' }, (p: any) => p),
      [`when: "c" is not a valid value of 'u' ("a", "b")`],
    ],
    [
      'when without values',
      () => anyBase().when({ env: [] }, (p: any) => p),
      ["when: 'env' has no values"],
    ],

    // external inputs
    [
      'bad external sources',
      () =>
        base().stage(
          s({
            name: 'bad',
            input: {
              'c.d': external(V, { title: '?', from: [person(), callback()] }),
              t: external(V, { title: ' ' }),
              u: external(V, { title: undefined as any }),
              f: external(V, { title: '?', from: [] }),
              v: external(V, {
                title: '?',
                from: [poll({ url: 'see https://x', valuePath: ' ' })],
              }),
            },
          }),
        ),
      [
        "step 'bad' external input 'c.d': a callback input's key must be letters, digits, '_' or '-' (it is part of the URL)",
        "step 'bad' external input 't': title must not be empty",
        "step 'bad' external input 'u': title must not be empty",
        "step 'bad' external input 'f': from must list at least one source",
        "step 'bad' external input 'v': poll url must be an absolute http(s) URL",
        "step 'bad' external input 'v': poll valuePath must not be empty",
      ],
    ],

    // config
    [
      'a config entry that is not one',
      () => base().stage(s({ name: 's', config: ['nope'] })),
      ['step \'s\': config entries must be config("name", "description", schema)'],
    ],
    [
      'a config entry that is not representable',
      () => base().stage(s({ name: 's', config: [config('when', '', z.date())] })),
      [`step 's': config 'when' is not representable as JSON Schema (${dateRefusal})`],
    ],
    [
      'a whole entry with a secret, next to a ref into another entry',
      () => base().stage(s({ name: 's', config: [PUB], settings: { h: TGT.host } })),
      [
        "step 's': config 'pub' references secret 'S_KEY' (secretRef), so the step must declare it in secrets",
      ],
    ],
    [
      'a ref into a record of secretRefs',
      () => base().stage(s({ name: 's', settings: { a: (K as any).keys.foo } })),
      [
        "step 's': config 'k' references secret 'S_KEY' (secretRef), so the step must declare it in secrets",
      ],
    ],
    [
      'a config ref to an unknown field',
      () => base().stage(s({ name: 's', settings: { l: [(X as any).nope] } })),
      ["step 's': config reference 'x.nope': config 'x' has no field 'nope'"],
    ],

    // select
    [
      'select on a non-ref',
      () => anyBase().select('x', {}),
      [
        'select: the first argument must reference a z.discriminatedUnion in a config entry',
        'select: the first argument must be a config reference',
      ],
    ],
    [
      'select on a non-union',
      () => anyBase().select(NAME.$, {}),
      [
        'select: the first argument must reference a z.discriminatedUnion in a config entry',
        "select 'name': it must reference a z.discriminatedUnion in config 'name'",
      ],
    ],
    [
      'select with an unknown case',
      () =>
        anyBase().select(X.prod, {
          a: (q: any) => q,
          b: (q: any) => q,
          bogus: (q: any) => q,
        }),
      ["select 'x.prod': 'bogus' is not a value of mode (a, b)"],
    ],
    [
      'select on an entry with a bad name',
      () => anyBase().select(BadCfg.$, { a: (q: any) => q }),
      ["select 'Bad': config name 'Bad' must match /^[a-z][a-zA-Z0-9]*$/"],
    ],
    [
      'a select case that is not a function',
      () => anyBase().select(X.prod, { a: 1, b: (q: any) => q }),
      ["select: case 'a' is not a function", "select 'x.prod': no case for mode 'a'"],
    ],
    [
      'a select case that returns another pipeline',
      () => anyBase().select(X.prod, { a: () => base(), b: (q: any) => q }),
      [
        "select: case 'a' must return the pipeline it was given ((p, ref) => p.stage(...))",
      ],
    ],
    [
      'a narrowed ref missing its branch secret',
      () =>
        anyBase().select(X.prod, {
          a: (q: any, r: any) => q.stage(s({ name: 'da', settings: { t: r } })),
          b: (q: any) => q,
        }),
      [
        "step 'da': config 'x' references secret 'S_KEY' (secretRef), so the step must declare it in secrets",
      ],
    ],
    [
      'a narrowed ref used in the other case',
      () =>
        anyBase().select(X.prod, {
          a: (q: any) => q,
          b: (q: any) =>
            q.stage(s({ name: 'late', settings: { t: leaked }, secrets: [S] })),
        }),
      [
        "step 'late': config reference 'x.prod' is narrowed to the select case 'x.prod.mode=a' and can only be used inside it",
      ],
    ],
    [
      'a select on a narrowed ref outside its case',
      () =>
        anyBase().select(X.prod, {
          a: (q: any) => q,
          b: (q: any) => q.select(leaked, { a: (r: any) => r, b: (r: any) => r }),
        }),
      [
        "select 'x.prod': the reference is narrowed to the case 'x.prod.mode=a' and can only be used inside it",
      ],
    ],
    [
      'a step only some select cases have, required later',
      () =>
        anyBase()
          .select(X.prod, {
            a: (q: any) =>
              q.stage(s({ name: 'n1' })).stage(s({ name: 'only', output: N })),
            b: (q: any) => q.stage(s({ name: 'n1' })),
          })
          .stage(s({ name: 'c', input: { n: N } })),
      [
        "step 'c': N (input 'n') is only maybe-available (produced inside a when block); declare it optional(N)",
      ],
    ],
    [
      'a step inside a when in one select case, required later',
      () =>
        anyBase()
          .select(X.prod, {
            a: (q: any) =>
              q.when({ env: 'prod' }, (w: any) => w.stage(s({ name: 'n', output: N }))),
            b: (q: any) => q.stage(s({ name: 'n', output: N })),
          })
          .stage(s({ name: 'c', input: { n: N } })),
      [
        "step 'c': N (input 'n') is only maybe-available (produced inside a when block); declare it optional(N)",
      ],
    ],
    [
      'select cases giving a step different outputs',
      () =>
        anyBase().select(X.prod, {
          a: (q: any) => q.stage(s({ name: 'n1', output: N })),
          b: (q: any) => q.stage(s({ name: 'n1' })),
        }),
      ["step 'n1': its select cases must give it the same output model"],
    ],
    [
      'a step name twice in one select case',
      () =>
        anyBase().select(X.prod, {
          a: (q: any) => q.stage(s({ name: 'n1' })).stage(s({ name: 'n1' })),
          b: (q: any) => q,
        }),
      ["duplicate step name 'n1'"],
    ],
    [
      'a step name in two selects',
      () =>
        anyBase()
          .select(X.prod, { a: (q: any) => q.stage(s({ name: 'n1' })), b: (q: any) => q })
          .select(X.prod, {
            a: (q: any) => q,
            b: (q: any) => q.stage(s({ name: 'n1' })),
          }),
      ["duplicate step name 'n1'"],
    ],

    // failure handlers
    [
      'a failure handler that is not a step',
      () => anyBase().stage(make).onFailure('x'),
      ['onFailure: handler is not a step'],
    ],
    [
      'a failure handler name',
      () =>
        anyBase()
          .stage(make)
          .onFailure(s({ name: 'Bad' })),
      [`onFailure: step name 'Bad' must be lower-case kebab (${kebab})`],
    ],
    [
      'a failure handler named like a stage step',
      () =>
        anyBase()
          .stage(make)
          .onFailure(s({ name: 'make', input: { ctx: FailureContext } })),
      ["duplicate step name 'make'"],
    ],
    [
      'a failure handler input that is not FailureContext',
      () => anyBase().stage(make).onFailure(use),
      [
        "step 'use': a failure handler's inputs must all be FailureContext (input 'art' is not)",
      ],
    ],
    [
      'a failure handler scope outside the pipeline',
      () =>
        anyBase()
          .stage(make)
          .onFailure(fh, { steps: [s({ name: 'ghost' })] }),
      ["onFailure(fh): 'ghost' is not a step of this pipeline"],
    ],

    // triggers
    [
      'two push triggers',
      () =>
        anyBase()
          .onPush(() => ({}))
          .onPush(() => ({})),
      ["duplicate trigger 'push' (onPush may be used once)"],
    ],
    [
      'two polls with the default name',
      () =>
        base()
          .onPoll(pollCfg, toInput)
          .onPoll({ ...pollCfg, url: 'u2' }, toInput),
      ["duplicate trigger 'poll' (give each onPoll and onWebhook a distinct name)"],
    ],
    [
      'a poll and a webhook with one name',
      () =>
        base()
          .onPoll({ ...pollCfg, name: 'x' }, toInput)
          .onWebhook({ name: 'x', verify: bearer(T) }, () => null),
      ["duplicate trigger 'x' (give each onPoll and onWebhook a distinct name)"],
    ],
    [
      'a poll header naming an undeclared secret',
      () =>
        base().onPoll(
          { ...pollCfg, secrets: [T], headers: { A: '$SECRET:OTHER' } as any },
          toInput,
        ),
      [
        "poll 'poll': header 'A' references secret 'OTHER', which is not in the poll's secrets",
      ],
    ],
    [
      'a poll name',
      () => base().onPoll({ ...pollCfg, name: 'Bad' }, toInput),
      ["poll trigger name 'Bad' must be lower-case kebab"],
    ],
    [
      'a poll interval',
      () => base().onPoll({ ...pollCfg, intervalSeconds: 0 }, toInput),
      ["poll 'poll': intervalSeconds must be a positive integer"],
    ],
    [
      'a poll secret name',
      () => base().onPoll({ ...pollCfg, secrets: [secret('lower', 'x')] }, toInput),
      ["poll 'poll': secret name 'lower' must match /^[A-Z_][A-Z0-9_]*$/"],
    ],
    [
      'a webhook name',
      () => base().onWebhook({ name: 'Bad', verify: bearer(T) }, () => null),
      ["webhook trigger name 'Bad' must be lower-case kebab"],
    ],
    [
      'a webhook secret name',
      () =>
        base().onWebhook({ name: 'h', verify: bearer(secret('lower', 'x')) }, () => null),
      ["webhook 'h': secret name 'lower' must match /^[A-Z_][A-Z0-9_]*$/"],
    ],
    [
      'an empty signature header',
      () =>
        base().onWebhook(
          { name: 'h', verify: hmacSha256({ secret: T, header: '' }) },
          () => null,
        ),
      ["webhook 'h': the signature header must not be empty"],
    ],
  ];
  test.each(refused)('refuses %s', (_name, p, expected) =>
    expect(problems(p())).toEqual(expected as string[]),
  );

  test('pipeline input must be a model', () =>
    expect(problems(pipeline({ name: 't', input: z.object({}) as any }))).toEqual([
      'pipeline input must be a model (model("Name", z.object({...})))',
    ]));
  test.each([null, undefined, {}])('%p is not a pipeline', (p) =>
    expect(() => describePipeline(p, { harness })).toThrow(
      'not a pipeline (the entry point must default-export pipeline({...})...)',
    ),
  );
});

test.each([
  ['non-finite number in canonical JSON: NaN', NaN],
  ['non-finite number in canonical JSON: Infinity', { a: Infinity }],
  ['non-finite number in canonical JSON: -Infinity', [-Infinity]],
  ['bigint is not JSON', 1n],
  ['function is not JSON', { f: () => 1 }],
  ['symbol is not JSON', { s: Symbol() }],
] as [string, unknown][])('canonicalJson refuses: %s', (message, v) =>
  expect(() => canonicalJson(v)).toThrow(message),
);

test('Source library model is a dir fileRef carrier', () => {
  expect(Source.name).toBe('Source');
});

describe('retention', () => {
  const manifestOf = (p: unknown) => describePipeline(p, { harness });
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
        retention: { keepLatestRuns: 5, minRunAgeDays: 30, buildGraceDays: 0 },
      }).stage(make),
    );
    expect(m.retention).toEqual({
      keepLatestRuns: 5,
      minRunAgeDays: 30,
      buildGraceDays: 0,
    });
    expect(Manifest.safeParse(m).success).toBe(true);
  });
  test.each([
    ['keepLatestRuns', -1],
    ['minRunAgeDays', 1.5],
    ['buildGraceDays', NaN],
  ] as const)('%s %p is refused', (key, value) =>
    expect(
      problems(
        pipeline({ name: 't', input: Req, retention: { [key]: value } }).stage(make),
      ),
    ).toEqual([`retention.${key} must be a non-negative integer`]),
  );
});
