// Secret masking: the rules, then a step run in a child process that leaks its secret every way it can.
import { describe, expect, test } from 'bun:test';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { lineMasker, maskPatterns, maskText } from '../../src/harness/mask.ts';
import { SDK, tmp } from './helpers.ts';

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
      ['-----BEGIN KEY-----\nAAAABBBBCCCC\n-----END KEY-----'],
      'key: AAAABBBBCCCC',
      'key: ***',
    ],
    ['an unset secret masks nothing', [undefined], 'nothing here', 'nothing here'],
  ] as [string, (string | undefined)[], string, string][])(
    '%s',
    (_, values, text, masked) =>
      expect(maskText(text, maskPatterns(values))).toBe(masked),
  );

  test('a value split across writes is still masked', () => {
    const out: string[] = [];
    const m = lineMasker(maskPatterns(['s3cr3t']), (t) => out.push(t));
    for (const chunk of ['token=s3', 'cr', '3t\nnext ', 'line s3cr3t']) m.write(chunk);
    m.flush();
    expect(out.join('')).toBe('token=***\nnext line ***');
  });
});

describe('a step with a secret', () => {
  const SECRET = 'hunter2-very-secret';
  const script = (dir: string) => {
    const file = join(dir, 'leak.ts');
    writeFileSync(
      file,
      `import { implement, model, pipeline, secret, step, z } from ${JSON.stringify(join(SDK, 'src/index.ts'))};
import { runHarness } from ${JSON.stringify(join(SDK, 'src/harness/main.ts'))};
const LEAKY = secret('LEAKY', 'Leaked on purpose.');
const leak = implement(step({ name: 'leak', description: '', image: 'alpine', secrets: [LEAKY] }), async (_, ctx) => {
  ctx.log('ctx.log ' + ctx.secrets.LEAKY);
  console.log('console.log ' + ctx.secrets.LEAKY);
  console.error('console.error ' + ctx.secrets.LEAKY);
  await ctx.exec(['sh', '-c', 'echo exec-out $LEAKY; echo exec-err $LEAKY >&2']);
  await ctx.sh('printf "sh-out %s\\\\n" "$LEAKY"');
  throw new Error('failed with ' + ctx.secrets.LEAKY);
});
const p = pipeline({ name: 't', input: model('In', z.object({})) }).stage(leak);
process.exit(await runHarness({ pipeline: p, harness: { bun: 'x', sdk: 'x', sdkHash: 'x' } } as never, ['run', 'leak']));
`,
    );
    return file;
  };

  test('never shows the value: logs, console, commands and the failure message', () => {
    const dir = tmp('mask');
    writeFileSync(join(dir, 'input.json'), '{}');
    const p = Bun.spawnSync([process.execPath, script(dir)], {
      env: { PATH: process.env.PATH ?? '', OLVE_DIR: dir, LEAKY: SECRET },
    });
    const out = p.stdout.toString();
    const err = p.stderr.toString();
    expect(out + err).not.toContain(SECRET);
    for (const where of ['ctx.log', 'console.error', 'exec-err'])
      expect(err).toContain(`${where} ***`);
    for (const where of ['console.log', 'exec-out', 'sh-out'])
      expect(out).toContain(`${where} ***`);
    expect(out).toContain('failed with ***');
    expect(p.exitCode).toBe(1);
  });
});
