// Secret masking: the rules, then a step that leaks its secret every way it can, run the way the step
// binary runs it (supervised, in a child process).
import { afterAll, describe, expect, test } from 'bun:test';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { lineMasker, maskPatterns, maskText } from '../../src/harness/mask.ts';
import { SDK, tmp, removeTmpDirs } from './helpers.ts';

afterAll(removeTmpDirs);

describe('mask rules', () => {
  test.each([
    [
      'a value is masked wherever it appears',
      ['s3cr3t'],
      'a s3cr3t b s3cr3t',
      'a *** b ***',
    ],
    ['values shorter than 4 characters are left alone', ['abc'], 'abc abcd', 'abc abcd'],
    [
      'the longer of two overlapping values wins',
      ['token', 'token-long'],
      'token-long',
      '***',
    ],
    [
      'a multi-line value is masked line by line',
      ['-----BEGIN KEY-----\r\nAAAABBBBCCCC\r\n-----END KEY-----'],
      'key: AAAABBBBCCCC',
      'key: ***',
    ],
    [
      'a value with a lone \\r is masked per part',
      ['abcdefgh\rijklmnop'],
      'v abcdefgh\rijklmnop',
      'v ***\r***',
    ],
    ['an unset secret masks nothing', [undefined], 'nothing here', 'nothing here'],
  ] as [string, (string | undefined)[], string, string][])(
    '%s',
    (_, values, text, masked) =>
      expect(maskText(text, maskPatterns(values))).toBe(masked),
  );

  test.each([
    [
      'split across writes',
      ['token=s3', 'cr', '3t\nnext ', 'line s3cr3t'],
      'token=***\nnext line ***',
    ],
    [
      'split at the 64 KB mark of a long line',
      ['A'.repeat(65530) + 's3c', 'r3t\n'],
      'A'.repeat(65530) + '***\n',
    ],
  ])('a value %s is still masked', (_, chunks, expected) => {
    const out: string[] = [];
    const m = lineMasker(maskPatterns(['s3cr3t']), (t) => out.push(t));
    for (const chunk of chunks) m.write(chunk);
    m.flush();
    expect(out.join('')).toBe(expected);
  });
});

describe('a step with a secret, as the binary runs it', () => {
  const SECRET = 'hunter2-very-secret';
  const sdk = (p: string) => JSON.stringify(join(SDK, p));
  // The generated entry: supervise first, then the pipeline's own code (which leaks at import too).
  const script = (dir: string) => {
    const pipelineFile = join(dir, 'pipeline.ts');
    writeFileSync(
      pipelineFile,
      `import { writeSync } from 'node:fs';
import { implement, model, pipeline, secret, step, z } from ${sdk('src/index.ts')};
console.log('import ' + process.env.LEAKY);
const LEAKY = secret('LEAKY', 'Leaked on purpose.');
const saved = console.log;
const leak = implement(step({ name: 'leak', description: '', image: 'alpine', secrets: [LEAKY] }), async (_, ctx) => {
  const s = ctx.secrets.LEAKY;
  ctx.log('ctx.log ' + s);
  console.log('console.log ' + s);
  console.error('console.error ' + s);
  console.dir({ dir: s });
  console.trace('trace ' + s);
  saved('saved ' + s);
  await Bun.write(Bun.stdout, 'bun.write ' + s + '\\n');
  writeSync(1, 'writesync ' + s + '\\n');
  await ctx.exec(['sh', '-c', 'echo exec-out $LEAKY; echo exec-err $LEAKY >&2']);
  Bun.spawnSync(['sh', '-c', 'echo spawn-inherit $LEAKY'], { stdout: 'inherit' });
  ctx.link('https://example.com/?token=' + s, 'link ' + s);
  throw new Error('failed with ' + s);
});
export default pipeline({ name: 't', input: model('In', z.object({})) }).stage(leak);
`,
    );
    const entry = join(dir, 'entry.ts');
    writeFileSync(
      entry,
      `import { runHarness, supervise } from ${sdk('src/harness/index.ts')};
const supervised = await supervise(process.argv.slice(2));
if (supervised !== undefined) process.exit(supervised);
const { default: pipeline } = await import(${JSON.stringify(pipelineFile)});
process.exit(await runHarness({ pipeline, harness: { bun: 'x', sdk: 'x', sdkHash: 'x' } } as never, process.argv.slice(2)));
`,
    );
    return entry;
  };

  test('never shows the value, whichever way the step prints it', () => {
    const dir = tmp('mask');
    writeFileSync(join(dir, 'input.json'), '{}');
    const p = Bun.spawnSync([process.execPath, script(dir), 'run', 'leak'], {
      env: {
        PATH: process.env.PATH ?? '',
        OLVE_DIR: dir,
        LEAKY: SECRET,
        OLVE_SECRETS: 'LEAKY',
      },
    });
    const log = p.stdout.toString() + p.stderr.toString();
    const envelope = readFileSync(join(dir, 'work', 'output.json'), 'utf8');
    expect(log).not.toContain(SECRET);
    expect(envelope).not.toContain(SECRET);
    for (const where of [
      'import',
      'ctx.log',
      'console.log',
      'console.error',
      'trace',
      'saved',
      'bun.write',
      'writesync',
      'exec-out',
      'exec-err',
      'spawn-inherit',
      'failed with',
    ])
      expect(log).toContain(`${where} ***`);
    expect(log).toContain('dir: "***"');
    expect(JSON.parse(envelope).links).toEqual([
      { url: 'https://example.com/?token=***', label: 'link ***' },
    ]);
    expect(p.exitCode).toBe(1);
  });

  test('an error nothing caught fails the step', () => {
    const dir = tmp('uncaught');
    writeFileSync(join(dir, 'input.json'), '{}');
    const pipelineFile = join(dir, 'pipeline.ts');
    writeFileSync(
      pipelineFile,
      `import { implement, model, pipeline, step, z } from ${sdk('src/index.ts')};
const s = implement(step({ name: 's', description: '', image: 'alpine' }), async () => {
  void Promise.reject(new Error('nobody caught this'));
  await Bun.sleep(10);
});
export default pipeline({ name: 't', input: model('In', z.object({})) }).stage(s);
`,
    );
    const entry = join(dir, 'entry.ts');
    writeFileSync(
      entry,
      `import { runHarness } from ${sdk('src/harness/index.ts')};
const { default: pipeline } = await import(${JSON.stringify(pipelineFile)});
process.exit(await runHarness({ pipeline, harness: { bun: 'x', sdk: 'x', sdkHash: 'x' } } as never, ['run', 's']));
`,
    );
    const p = Bun.spawnSync([process.execPath, entry], {
      env: { PATH: process.env.PATH ?? '', OLVE_DIR: dir },
    });
    const envelope = JSON.parse(readFileSync(join(dir, 'work', 'output.json'), 'utf8'));
    expect(envelope).toEqual({
      status: 'failed',
      error: {
        kind: 'handler',
        message: "step 's' failed: an error nothing caught: nobody caught this",
      },
    });
    expect(p.exitCode).toBe(1);
  });
});
