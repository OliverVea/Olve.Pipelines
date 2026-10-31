// Typed pipeline config: describe (manifest + checks), delivery through input.json's $config, testHandler.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, test } from 'bun:test';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  config,
  implement,
  model,
  pipeline,
  secret,
  secretRef,
  step,
  z,
} from '../../src/index.ts';
import {
  describePipeline,
  DescribeError,
  secretRefsOf,
} from '../../src/describe/describe.ts';
import { Manifest } from '../../src/describe/manifest-schema.ts';
import { allSteps, runHarness, type HarnessConfig } from '../../src/harness/main.ts';
import { pipelineData } from '../../src/builder/pipeline.ts';
import { EXIT } from '../../src/harness/exit.ts';
import { testHandler } from '../../src/testing/index.ts';
import { captureIO, tmp, withEnv } from './helpers.ts';

const harness = { bun: '1.4.3', sdk: '0.0.0', sdkHash: 'sha256:' + 'a'.repeat(64) };

const DEX_SECRET = secret('DEX_CLIENT_SECRET', 'Dex client secret.');
const AUTH = config(
  'auth',
  'How users authenticate.',
  z.discriminatedUnion('mode', [
    z.object({ mode: z.literal('none') }),
    z.object({
      mode: z.literal('dex'),
      issuer: z.string(),
      clientSecret: secretRef(DEX_SECRET),
    }),
  ]),
);
const TARGET = config(
  'target',
  'Where to deploy.',
  z.object({ host: z.string(), namespace: z.string().default('olve') }),
);
const TELEMETRY = config(
  'telemetry',
  'Exporter.',
  z.object({ exporter: z.enum(['none', 'otlp']) }).default({ exporter: 'none' }),
);

const Req = model('Req', z.object({ sha: z.string() }));
const Out = model('Out', z.object({ text: z.string() }));
const deploy = step({
  name: 'deploy',
  description: '',
  image: 'alpine',
  input: { req: Req },
  output: Out,
  config: [TARGET, AUTH, TELEMETRY],
  secrets: [DEX_SECRET],
});
const plain = step({
  name: 'plain',
  description: '',
  image: 'alpine',
  input: { req: Req },
});

const describeOf = (p: unknown) => {
  const names = allSteps(pipelineData(p)).map((s) => s.name);
  return describePipeline(p, {
    handlerNames: names,
    harness,
  });
};
const problems = (p: unknown): string[] => {
  try {
    describeOf(p);
    return [];
  } catch (e) {
    if (e instanceof DescribeError) return [...e.problems];
    throw e;
  }
};

describe('describe', () => {
  test('emits config entries (sorted, input view, required) and per-step configEntries', () => {
    const m = describeOf(
      pipeline({ name: 't', input: Req }).stage(deploy).stage(plain),
    ) as any;
    expect(Manifest.safeParse(m).success).toBe(true);
    expect(m.config.map((c: any) => [c.name, c.required])).toEqual([
      ['auth', true],
      ['target', true],
      ['telemetry', false],
    ]);
    expect(m.config[1].schema).toEqual({
      properties: {
        host: { type: 'string' },
        namespace: { default: 'olve', type: 'string' },
      },
      required: ['host'],
      type: 'object',
    });
    expect(m.config[0].description).toBe('How users authenticate.');
    expect(m.stages[0].steps[0].configEntries).toEqual(['auth', 'target', 'telemetry']);
    expect('configEntries' in m.stages[1].steps[0]).toBe(false);
    expect(secretRefsOf(m.config[0].schema)).toEqual(['DEX_CLIENT_SECRET']);
  });

  test('omits config when no step declares any', () => {
    const m = describeOf(pipeline({ name: 't', input: Req }).stage(plain)) as any;
    expect('config' in m).toBe(false);
  });

  test('a step receiving a secretRef must declare the secret', () => {
    const bad = step({
      name: 'bad',
      description: '',
      image: 'alpine',
      input: { req: Req },
      config: [AUTH],
    });
    expect(problems(pipeline({ name: 't', input: Req }).stage(bad))).toContain(
      "step 'bad': config 'auth' references secret 'DEX_CLIENT_SECRET' (secretRef), so the step must declare it in secrets",
    );
  });

  test('config names, one schema per name, reserved input keys', () => {
    const badName = config('Bad-name', '', z.string());
    const other = config('target', 'Where to deploy.', z.object({ host: z.number() }));
    const s1 = step({
      name: 'bad',
      description: '',
      image: 'alpine',
      input: { req: Req, $x: Req } as any,
      config: [badName],
    });
    const s2 = step({
      name: 'other',
      description: '',
      image: 'alpine',
      input: { req: Req },
      config: [other],
    });
    const ps = problems(
      pipeline({ name: 't', input: Req })
        .stage(deploy)
        .stage(s1 as any)
        .stage(s2 as any),
    );
    expect(ps.some((p) => p.includes("config name 'Bad-name' must match"))).toBe(true);
    expect(ps).toContain(
      "two different config entries are named 'target' (one description and schema per config name)",
    );
    expect(ps).toContain("step 'bad': input '$x': keys starting with '$' are reserved");
  });
});

describe('delivery', () => {
  const impl = implement(deploy, async (_, ctx) => ({
    text: JSON.stringify(ctx.config),
  }));
  const cfg: HarnessConfig = {
    pipeline: pipeline({ name: 't', input: Req }).stage(deploy).stage(plain),
    handlers: {
      deploy: async () => ({ default: impl }),
      plain: async () => ({ default: implement(plain, () => undefined) }),
    },
    harness,
  };
  const run = async (name: string, input: unknown) => {
    const dir = tmp('config');
    writeFileSync(join(dir, 'input.json'), JSON.stringify(input));
    const io = captureIO();
    const code = await withEnv({ OLVE_DIR: dir, OLVE_RESULT_URL: undefined }, () =>
      runHarness(cfg, ['run', name], io),
    );
    const out = join(dir, 'work', 'output.json');
    return {
      code,
      envelope: existsSync(out) ? JSON.parse(readFileSync(out, 'utf8')) : undefined,
    };
  };

  test('$config is parsed into ctx.config with defaults applied', async () => {
    const r = await run('deploy', {
      req: { sha: 'x' },
      $config: { target: { host: 'h' }, auth: { mode: 'none' }, unrelated: 1 },
    });
    expect(r.code).toBe(0);
    expect(JSON.parse(r.envelope.output.text)).toEqual({
      target: { host: 'h', namespace: 'olve' },
      auth: { mode: 'none' },
      telemetry: { exporter: 'none' },
    });
  });

  test('a missing required entry fails as input (exit 65)', async () => {
    const r = await run('deploy', {
      req: { sha: 'x' },
      $config: { target: { host: 'h' } },
    });
    expect(r.code).toBe(EXIT.inputInvalid);
    expect(r.envelope.error.kind).toBe('input');
    expect(r.envelope.error.message).toContain('config.auth');
    expect(r.envelope.error.message).toContain("config entry 'auth' is not set");
  });

  test('a step without config ignores $config', async () => {
    const r = await run('plain', {
      req: { sha: 'x' },
      $config: { target: { host: 'h' } },
    });
    expect(r.code).toBe(0);
  });

  test('testHandler takes config', async () => {
    const r = await testHandler(
      impl,
      { req: { sha: 'x' } },
      {
        config: {
          target: { host: 'h', namespace: 'n' },
          auth: {
            mode: 'dex',
            issuer: 'i',
            clientSecret: { secret: 'DEX_CLIENT_SECRET' },
          },
        },
      },
    );
    expect(r.status).toBe('succeeded');
    if (r.status === 'succeeded')
      expect(JSON.parse(r.output.text).auth).toEqual({
        mode: 'dex',
        issuer: 'i',
        clientSecret: { secret: 'DEX_CLIENT_SECRET' },
      });
  });
});
