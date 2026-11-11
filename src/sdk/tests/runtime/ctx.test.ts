// ctx pieces, exercised through testHandler (the same validation path as a real run).
/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterAll, describe, expect, spyOn, test } from 'bun:test';
import {
  chmodSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  readlinkSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import {
  implement,
  model,
  secret,
  step,
  steps,
  z,
  fileRef,
  Source,
} from '../../src/index.ts';
import { testFileRef, testHandler } from '../../src/testing/index.ts';
import {
  createSh,
  detectShell,
  ShellNotFoundError,
  isEmbeddedScript,
  CommandError,
  CommandStalledError,
} from '../../src/ctx/sh.ts';
import { createRetry, formatLog } from '../../src/ctx/index.ts';
import { createFetch, createOutput, type FilesDeps } from '../../src/ctx/files.ts';
import { sha256File, sha256Hex } from '../../src/util/hash.ts';
import { dirEntries, writeTar, type TarEntry } from '../../src/util/tar.ts';
import { checkout as checkoutImpl } from '../../examples/self-deploy/steps/checkout.ts';
import { build as buildImpl } from '../../examples/self-deploy/steps/build.ts';
import { deployBeta as deployBetaImpl } from '../../examples/self-deploy/steps/deploy-beta.ts';
import { test as testImpl } from '../../examples/self-deploy/steps/test.ts';
import { EXAMPLE, SHA, tmp, withEnv, removeTmpDirs } from './helpers.ts';

afterAll(removeTmpDirs);

const Out = model('Out', z.object({ text: z.string() }));
const KEY = secret('API_KEY', 'an api key');
const plain = step({
  name: 'plain',
  description: '',
  image: 'alpine',
  input: { cmd: z.string() },
  output: Out,
  secrets: [KEY],
});

const servers: { stop(force?: boolean): void }[] = [];
afterAll(() => servers.forEach((s) => s.stop(true)));

const messageOf = (p: Promise<unknown>) =>
  p.then(
    () => 'resolved',
    (e: Error) => e.message,
  );
type TestSh = ReturnType<typeof newSh>;
const newSh = (prefix: string, signal = new AbortController().signal) => {
  const dir = tmp(prefix);
  return { dir, ...createSh({ tmpDir: dir, signal }) };
};
const FILE_ENV = {
  OLVE_FILE_URLS: undefined,
  OLVE_FILES_DIR: undefined,
  OLVE_FILES_BASE_URL: undefined,
  OLVE_FILES_TOKEN: undefined,
};
/** `ctx.files.fetch` of attempt 2 of `producer` in run 3, with recorded logs and backoff. */
function fetcher(root = tmp('fetch')) {
  const signal = new AbortController().signal;
  const logs: string[] = [];
  const delays: number[] = [];
  const deps: FilesDeps = {
    run: {
      pipeline: 't',
      id: 't',
      number: 3,
      step: 'producer',
      attempt: 2,
      version: 'v',
    },
    workFiles: join(root, 'work/files'),
    tmpDir: join(root, 'tmp'),
    root,
    signal,
    log: (m, f) => logs.push(formatLog(m, f)),
    sleep: async (ms) => void delays.push(ms),
  };
  return { root, deps, logs, delays, fetch: createFetch(deps) };
}
const refTo = (
  content: string,
  at: Partial<{ run: number; step: string; attempt: number }> = {},
) => testFileRef({ run: 3, step: 'producer', attempt: 1, path: 'a.txt', content, ...at });

describe('ctx.sh / ctx.exec', () => {
  test('command string, captured text, env, failures carry the exit code', async () => {
    const impl = implement(plain, async ({ cmd }, ctx) => {
      await ctx.sh('true');
      const text = await ctx.sh.text(cmd, { env: { GREETING: 'hello' } });
      await ctx
        .sh('exit 3')
        .catch((e: CommandError) => ctx.log('failed', { code: e.exitCode }));
      return { text };
    });
    const r = await withEnv({ OLVE_T_INHERIT: 'inherited' }, () =>
      testHandler(impl, { cmd: 'echo "$GREETING world $OLVE_T_INHERIT"' }),
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { text: 'hello world inherited' },
    });
    expect(r.logs).toContain('failed code=3');
  });
  test.each<[string, (s: TestSh) => Promise<unknown>, string]>([
    ['exit 3', (s) => s.sh('exit 3'), 'command `exit 3` failed with exit code 3'],
    ['exec', (s) => s.exec(['/bin/false']), '`/bin/false` failed with exit code 1'],
    [
      'whitespace collapsed',
      (s) => s.sh('true\n   false'),
      'command `true false` failed with exit code 1',
    ],
    [
      'truncated to 120 chars',
      (s) => s.sh('false #' + 'x'.repeat(200)),
      'command `false #' + 'x'.repeat(112) + '…` failed with exit code 1',
    ],
    [
      'script',
      (s) => {
        writeFileSync(join(s.dir, 'fail.sh'), 'exit 2\n');
        return s.sh(join(s.dir, 'fail.sh'));
      },
      'script fail.sh failed with exit code 2',
    ],
    ['exec of nothing', (s) => s.exec([]), 'ctx.exec needs at least the program'],
  ])('a failure names the command: %s', async (_, run, message) => {
    expect(await messageOf(run(newSh('fail')))).toBe(message);
  });
  test('a command killed by a signal reports the signal, not an exit code', async () => {
    const e = (await newSh('kill')
      .exec(['/bin/sh', '-c', 'kill -9 $$'])
      .catch((x) => x)) as CommandError;
    expect(e).toBeInstanceOf(CommandError);
    expect(e.message).toBe('`/bin/sh -c kill -9 $$` failed with signal SIGKILL');
    expect([e.exitCode, e.signalCode]).toEqual([null, 'SIGKILL']);
  });
  test('without capture, output goes straight to the step log', async () => {
    const s = newSh('direct');
    const child = Bun.spawnSync([
      process.execPath,
      '-e',
      `import { createSh } from ${JSON.stringify(join(import.meta.dir, '../../src/ctx/sh.ts'))};
       await createSh({ tmpDir: ${JSON.stringify(s.dir)}, signal: new AbortController().signal })
         .exec(['/bin/echo', 'marker']);`,
    ]);
    expect(child.stdout.toString()).toBe('marker\n');
  });
  test('a command string gets its args as $1, $2…, unquoted by the shell', async () => {
    const impl = implement(plain, async ({ cmd }, ctx) => ({
      text: await ctx.sh.text(cmd, { args: ['a b', '$HOME'] }),
    }));
    const r = await testHandler(impl, { cmd: 'printf "%s|%s" "$1" "$2"' });
    expect(r).toMatchObject({ status: 'succeeded', output: { text: 'a b|$HOME' } });
  });
  test('embedded scripts are copied out and run with args; the harness env is not inherited', async () => {
    const dir = tmp('script');
    const script = join(dir, 's.sh');
    writeFileSync(
      script,
      '#!/bin/sh\necho "args:$1,$2 url:${OLVE_RESULT_URL:-unset} run:${OLVE_RUN:-unset}"\n',
    );
    expect(isEmbeddedScript(script)).toBe(true);
    expect(isEmbeddedScript('echo hi')).toBe(false);
    expect(isEmbeddedScript('/$bunfs/root/x.sh')).toBe(true);
    const impl = implement(plain, async (_i, ctx) => ({
      text: await ctx.sh.text(script, { args: ['a', 'b'] }),
    }));
    const r = await withEnv(
      { OLVE_RESULT_URL: 'http://secret/?X-Amz-Credential=1', OLVE_RUN: '7' },
      () => testHandler(impl, { cmd: '' }),
    );
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { text: 'args:a,b url:unset run:unset' },
    });
    expect(existsSync(join(r.dir, 'tmp', 'scripts', '1-s.sh'))).toBe(true);
  });
  test.each([
    ['interpreter and argument', '#!/bin/echo hi\n', (f: string) => `hi ${f} a b`],
    ['interpreter alone', '#!/bin/echo\n', (f: string) => `${f} a b`],
    ['argument trimmed', '#!/bin/echo hi \n', (f: string) => `hi ${f} a b`],
    ['space after #!', '#! /bin/echo hi\n', (f: string) => `hi ${f} a b`],
    ['no shebang: the shell', 'echo ns:$1\n', () => 'ns:a'],
    ['missing interpreter: the shell', '#!/nope/interp\necho fb\n', () => 'fb'],
  ])('a script runs with its shebang: %s', async (_, body, expected) => {
    const s = newSh('shebang');
    writeFileSync(join(s.dir, 'x.sh'), body);
    expect(await s.sh.text(join(s.dir, 'x.sh'), { args: ['a', 'b'] })).toBe(
      expected(join(s.dir, 'scripts', '1-x.sh')),
    );
  });
  test('only an absolute, existing, whitespace-free .sh/.bash file is a script; each is copied out once', async () => {
    const s = newSh('scripts');
    for (const f of ['a.sh', 'b.bash', 'x.sh.txt', 'a b.sh'])
      writeFileSync(join(s.dir, f), 'true\n');
    mkdirSync(join(s.dir, 'd.sh'));
    expect(
      ['a.sh', 'b.bash', 'x.sh.txt', 'a b.sh', 'd.sh', 'missing.sh'].map((f) =>
        isEmbeddedScript(join(s.dir, f)),
      ),
    ).toEqual([true, true, false, false, false, false]);
    expect(isEmbeddedScript('/bin/true')).toBe(false);
    expect(isEmbeddedScript('a.sh')).toBe(false);
    for (const f of ['a.sh', 'b.bash', 'a.sh']) await s.sh(join(s.dir, f));
    expect(readdirSync(join(s.dir, 'scripts')).sort()).toEqual(['1-a.sh', '2-b.bash']);
  });
  test('shell detection: first existing candidate, clear error when none', async () => {
    expect(detectShell()).toBe('/bin/sh');
    expect(detectShell(['/nope/sh', '/bin/sh'])).toBe('/bin/sh');
    expect(() => detectShell(['/nope/sh'])).toThrow(ShellNotFoundError);
    const { sh } = createSh({
      tmpDir: tmp('noshell'),
      signal: new AbortController().signal,
      shellCandidates: ['/nope/sh', '/busybox/nope'],
    });
    expect(await messageOf(sh('echo hi'))).toBe(
      'no shell found in this image (looked for /bin/sh, /busybox/sh, /bin/bash). ' +
        'ctx.sh needs a shell: use an image that has one, or run binaries directly with ctx.exec([...]).',
    );
  });
  test('exec runs binaries without a shell', async () => {
    const impl = implement(plain, async (_i, ctx) => {
      await ctx.exec(['/bin/true']);
      await expect(ctx.exec(['/bin/false'])).rejects.toThrow(/exit code 1/);
      await expect(ctx.exec(['/does/not/exist'])).rejects.toThrow(/cannot start/);
      return { text: 'ok' };
    });
    expect((await testHandler(impl, { cmd: '' })).status).toBe('succeeded');
  });
  test('idleTimeoutSeconds: a quiet command is killed, a chatty one runs to completion', async () => {
    const { sh, exec } = createSh({
      tmpDir: tmp('idle'),
      signal: new AbortController().signal,
    });
    const t0 = Date.now();
    // The watchdog reads the pipes, and still forwards them to the step's log.
    const forwarded = { out: '', err: '' };
    const out = spyOn(process.stdout, 'write').mockImplementation((c: any) => {
      forwarded.out += Buffer.from(c).toString();
      return true;
    });
    const err = spyOn(process.stderr, 'write').mockImplementation((c: any) => {
      forwarded.err += Buffer.from(c).toString();
      return true;
    });
    const stalled = await exec(['/bin/sh', '-c', 'echo started; echo e >&2; sleep 30'], {
      idleTimeoutSeconds: 0.3,
    })
      .catch((e) => e)
      .finally(() => {
        out.mockRestore();
        err.mockRestore();
      });
    expect(forwarded).toEqual({ out: 'started\n', err: 'e\n' });
    expect(Date.now() - t0).toBeLessThan(5000);
    expect(stalled).toBeInstanceOf(CommandStalledError);
    expect(stalled).toBeInstanceOf(CommandError);
    expect((stalled as Error).message).toMatch(
      /produced no output for 0.3 s and was killed/,
    );
    // Output on either stream re-arms the watchdog: 6 × 0.1 s of output outlives a 0.3 s window.
    await exec(
      [
        '/bin/sh',
        '-c',
        'for i in 1 2 3; do echo $i; sleep 0.1; echo e$i >&2; sleep 0.1; done',
      ],
      { idleTimeoutSeconds: 0.3 },
    );
    expect(await sh.text('sleep 0.1; echo captured', { idleTimeoutSeconds: 2 })).toBe(
      'captured',
    );
    await expect(
      exec(['/bin/sh', '-c', 'echo x; exit 4'], { idleTimeoutSeconds: 2 }),
    ).rejects.toThrow(/exit code 4/);
  });
  test('idleTimeoutSeconds: TERM is escalated to KILL; children holding the pipes do not hang the call', async () => {
    const { exec } = createSh({
      tmpDir: tmp('idle-kill'),
      signal: new AbortController().signal,
    });
    const t0 = Date.now();
    // The shell ignores TERM and a background child keeps stdout open.
    const p = exec(
      ['/bin/sh', '-c', "trap '' TERM; sleep 13 & while :; do sleep 1; done"],
      { idleTimeoutSeconds: 0.2 },
    );
    await expect(p).rejects.toThrow(CommandStalledError);
    expect(Date.now() - t0).toBeLessThan(15_000);
  }, 20_000);
  test('a background child holding the pipes does not hang a finished command', async () => {
    const { sh, exec } = createSh({
      tmpDir: tmp('pipe-grace'),
      signal: new AbortController().signal,
      pipeGraceMs: 200,
    });
    const t0 = Date.now();
    expect(await sh.text('sleep 13 & echo done')).toBe('done');
    await exec(['/bin/sh', '-c', 'sleep 13 & echo done'], { idleTimeoutSeconds: 30 });
    await expect(sh.text('sleep 13 & exit 3')).rejects.toThrow(/exit code 3/);
    expect(Date.now() - t0).toBeLessThan(5000);
  });
  test('signal: cancellation kills the running command', async () => {
    const ac = new AbortController();
    const impl = implement(plain, async (_i, ctx) => {
      setTimeout(() => ac.abort('SIGTERM'), 100);
      await ctx.sh('sleep 5');
      return { text: 'not reached' };
    });
    const t0 = Date.now();
    const r = await testHandler(impl, { cmd: '' }, { signal: ac.signal });
    expect(Date.now() - t0).toBeLessThan(3000);
    expect(r).toMatchObject({
      status: 'failed',
      error: { kind: 'handler' },
      exitCode: 143,
    });
  });
});

describe('after cancellation ctx starts no new work', () => {
  test.each([
    [
      'sh',
      (s: TestSh) => s.sh('touch m', { cwd: s.dir }),
      'command `touch m` not started: the step was cancelled',
    ],
    [
      'exec',
      (s: TestSh) => s.exec(['/bin/sh', '-c', 'touch m'], { cwd: s.dir }),
      '`/bin/sh -c touch m` not started: the step was cancelled',
    ],
  ])('%s refuses to start', async (_, run, message) => {
    const ac = new AbortController();
    ac.abort();
    const s = newSh('cancelled', ac.signal);
    expect(await messageOf(run(s))).toBe(message);
    expect(existsSync(join(s.dir, 'm'))).toBe(false);
  });
  test('an HTTP request in flight is aborted', async () => {
    const { promise: seen, resolve } = Promise.withResolvers<void>();
    const server = Bun.serve({
      port: 0,
      fetch() {
        resolve();
        return new Promise<Response>(() => {});
      },
    });
    servers.push(server);
    const ac = new AbortController();
    const impl = implement(plain, async (_i, ctx) => {
      void seen.then(() => ac.abort('SIGTERM'));
      await ctx.http.getJson(`http://localhost:${server.port}/hang`);
      return { text: 'not reached' };
    });
    const r = await testHandler(impl, { cmd: '' }, { signal: ac.signal });
    expect(r).toMatchObject({ status: 'failed', exitCode: 143 });
  });
  test('a retry wait rejects at once and fn is not called again', async () => {
    const ac = new AbortController();
    const retry = createRetry(ac.signal, () => queueMicrotask(() => ac.abort()));
    let calls = 0;
    const p = retry({ attempts: 3, delaySeconds: 3600 }, async () => {
      calls++;
      throw new Error('fails');
    });
    expect(await messageOf(p)).toBe('retry cancelled: the step was cancelled');
    expect(calls).toBe(1);
  });
});

describe('ctx.secrets', () => {
  test('typed to the declared set; read from options or env; clear error when missing', async () => {
    const keys = implement(plain, async (_i, ctx) => ({
      text: Object.keys(ctx.secrets).join(','),
    }));
    expect(await testHandler(keys, { cmd: '' })).toMatchObject({
      output: { text: 'API_KEY' },
    });
    const impl = implement(plain, async (_i, ctx) => ({ text: ctx.secrets.API_KEY }));
    expect(
      await testHandler(impl, { cmd: '' }, { secrets: { API_KEY: 'k1' } }),
    ).toMatchObject({ output: { text: 'k1' } });
    expect(
      await withEnv({ API_KEY: 'k2' }, () => testHandler(impl, { cmd: '' })),
    ).toMatchObject({ output: { text: 'k2' } });
    const r = await withEnv({ API_KEY: undefined }, () => testHandler(impl, { cmd: '' }));
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'handler' } });
    expect((r as any).error.message).toMatch(/secret API_KEY is not set/);
  });
  test('readText gives the text, readJson the parsed value', async () => {
    const f = join(tmp('read'), 'v.json');
    writeFileSync(f, '{"a":[1,"x"]}');
    const impl = implement(plain, async (_i, ctx) => ({
      text: JSON.stringify([await ctx.readText(f), await ctx.readJson(f)]),
    }));
    expect(await testHandler(impl, { cmd: '' })).toMatchObject({
      output: { text: JSON.stringify(['{"a":[1,"x"]}', { a: [1, 'x'] }]) },
    });
  });
});

describe('ctx.output and ctx.files.fetch', () => {
  const Files = model('Files', z.object({ a: fileRef(), b: fileRef(), d: fileRef() }));
  const producer = step({
    name: 'producer',
    description: '',
    image: 'alpine',
    output: Files,
  });
  const consumer = step({
    name: 'consumer',
    description: '',
    image: 'alpine',
    input: { files: Files },
    output: Out,
  });

  test('write / file / dir produce refs with run, step, attempt, path, sha256 (dirs as deterministic tars)', async () => {
    const src = tmp('out');
    writeFileSync(join(src, 'b.bin'), 'bee');
    mkdirSync(join(src, 'tree/sub'), { recursive: true });
    writeFileSync(join(src, 'tree/sub/x.txt'), 'x');
    const impl = implement(producer, async (_i, ctx) => ({
      a: await ctx.output.write('nested/a.txt', 'aaa'),
      b: await ctx.output.file(join(src, 'b.bin')),
      d: await ctx.output.dir(join(src, 'tree'), { name: 'tree' }),
    }));
    const r = await testHandler(impl, {}, { run: { number: 7, attempt: 2 } });
    expect(r.status).toBe('succeeded');
    const out = (r as any).output;
    expect(out.a).toEqual({
      run: 7,
      step: 'producer',
      attempt: 2,
      path: 'nested/a.txt',
      sha256: sha256File(join(r.filesDir, 'nested/a.txt')),
    });
    expect(out.b.path).toBe('b.bin');
    expect(out.d.path).toBe('tree/');
    expect(existsSync(join(r.filesDir, 'tree.tar'))).toBe(true);
    // determinism: same tree → same tar hash
    const again = await testHandler(impl, {}, { run: { number: 7, attempt: 2 } });
    expect((again as any).output.d.sha256).toBe(out.d.sha256);
  });
  test('a directory ref is a fixed, deterministic tar', async () => {
    const src = tmp('golden');
    const LONG_DIR = ['d', 'a'.repeat(250), 'b'.repeat(250), 'c'.repeat(250)].join('/');
    // n bytes in all; PAX records of 989-byte paths have a 3-digit length, 990+ a 4-digit one.
    const deep = (n: number) => `${LONG_DIR}/${'f'.repeat(n - LONG_DIR.length - 1)}`;
    const put = (rel: string, content: string | Uint8Array, mode = 0o644) => {
      mkdirSync(dirname(join(src, rel)), { recursive: true });
      writeFileSync(join(src, rel), content);
      chmodSync(join(src, rel), mode);
    };
    put('b.txt', 'bee\n', 0o640);
    put('run.sh', '#!/bin/sh\n', 0o700);
    put('empty', '');
    put('block', 'k'.repeat(512));
    put(
      'big.bin',
      Uint8Array.from({ length: 1.5 * 2 ** 20 }, (_, i) => i % 251),
    );
    put('sub/x.txt', 'x');
    put('sub.txt', 's');
    put('sub-y', 'y');
    put('p'.repeat(100), '100');
    for (const n of [989, 990, 993]) put(deep(n), String(n));
    mkdirSync(join(src, 'emptydir'));
    symlinkSync('b.txt', join(src, 'link'));
    symlinkSync('./'.repeat(47) + 'run.sh', join(src, 'link100'));
    symlinkSync('./'.repeat(46) + 'sub/x.txt', join(src, 'link101'));
    expect(Bun.spawnSync(['mkfifo', join(src, 'fifo')]).exitCode).toBe(0);

    const impl = implement(producer, async (_i, ctx) => ({
      a: await ctx.output.write('a', ''),
      b: await ctx.output.write('b', ''),
      d: await ctx.output.dir(src, { name: 'tree' }),
    }));
    const r = await testHandler(impl, {});
    expect(r.status).toBe('succeeded');
    const d = (r as any).output.d;

    // `tar -tvf` columns: mode, owner/group, size, date, time, name [-> target]
    const file = (mode: string, size: number, name: string, ...rest: string[]) => [
      mode,
      '0/0',
      String(size),
      '1970-01-01',
      '00:00',
      name,
      ...rest,
    ];
    const dir = (name: string) => file('drwxr-xr-x', 0, name + '/');
    const link = (name: string, target: string) =>
      file('lrwxrwxrwx', 0, name, '->', target);
    const expected = [
      file('-rw-r--r--', 4, 'b.txt'),
      file('-rw-r--r--', 1.5 * 2 ** 20, 'big.bin'),
      file('-rw-r--r--', 512, 'block'),
      dir('d'),
      dir(LONG_DIR.split('/').slice(0, 2).join('/')),
      dir(LONG_DIR.split('/').slice(0, 3).join('/')),
      dir(LONG_DIR),
      file('-rw-r--r--', 3, deep(989)),
      file('-rw-r--r--', 3, deep(990)),
      file('-rw-r--r--', 3, deep(993)),
      file('-rw-r--r--', 0, 'empty'),
      dir('emptydir'),
      link('link', 'b.txt'),
      link('link100', './'.repeat(47) + 'run.sh'),
      link('link101', './'.repeat(46) + 'sub/x.txt'),
      file('-rw-r--r--', 3, 'p'.repeat(100)),
      file('-rwxr-xr-x', 10, 'run.sh'),
      dir('sub'),
      file('-rw-r--r--', 1, 'sub-y'),
      file('-rw-r--r--', 1, 'sub.txt'),
      file('-rw-r--r--', 1, 'sub/x.txt'),
    ];
    const listing = Bun.spawnSync(['tar', '-tvf', join(r.filesDir, 'tree.tar')], {
      env: { ...process.env, TZ: 'UTC' },
    });
    expect(
      listing.stdout
        .toString()
        .trimEnd()
        .split('\n')
        .map((l) => l.split(/\s+/)),
    ).toEqual(expected);
    // Pinned: file refs, de-dup and imageBuild's content tag hash these exact bytes.
    expect(d.sha256).toBe(
      'f32c6e202c49bf0d9acd49f506fd786a0e2fa1052584a69934a57501bdb34f0c',
    );

    const pool = tmp('golden-pool');
    mkdirSync(join(pool, '1/producer/1'), { recursive: true });
    await Bun.$`cp -r ${r.filesDir} ${join(pool, '1/producer/1/files')}`;
    const { fetch } = fetcher();
    const got = await withEnv({ ...FILE_ENV, OLVE_FILES_DIR: pool }, () => fetch(d));
    for (const f of [
      'b.txt',
      'big.bin',
      'block',
      'empty',
      'run.sh',
      'sub/x.txt',
      deep(993),
    ])
      expect(readFileSync(join(got, f))).toEqual(readFileSync(join(src, f)));
    expect(statSync(join(got, 'run.sh')).mode & 0o111).toBe(0o111);
    expect(statSync(join(got, 'b.txt')).mode & 0o111).toBe(0);
    expect(['link', 'link100', 'link101'].map((l) => readlinkSync(join(got, l)))).toEqual(
      ['b.txt', './'.repeat(47) + 'run.sh', './'.repeat(46) + 'sub/x.txt'],
    );
    expect(readdirSync(join(got, 'emptydir'))).toEqual([]);
    expect(existsSync(join(got, 'fifo'))).toBe(false);
  });
  test.each([
    [
      '../x',
      (c: any) => c.output.write('../x', '1'),
      "ctx.output.write: '../x' is not a valid relative file name (no leading '/', no '..', no trailing '/')",
    ],
    [
      'x/',
      (c: any) => c.output.write('x/', '1'),
      "ctx.output.write: 'x/' is not a valid relative file name (no leading '/', no '..', no trailing '/')",
    ],
    [
      '/x',
      (c: any) => c.output.write('/x', '1'),
      "ctx.output.write: '/x' is not a valid relative file name (no leading '/', no '..', no trailing '/')",
    ],
    [
      'file named x/',
      (c: any, s: string) => c.output.file(join(s, 'f'), { name: 'x/' }),
      "ctx.output.file: 'x/' is not a valid relative file name (no leading '/', no '..', no trailing '/')",
    ],
    [
      'dir named ../t',
      (c: any, s: string) => c.output.dir(join(s, 'd'), { name: '../t' }),
      "ctx.output.dir: '../t' is not a valid relative file name (no leading '/', no '..', no trailing '/')",
    ],
    [
      'file of a dir',
      (c: any, s: string) => c.output.file(join(s, 'd')),
      (s: string) => `ctx.output.file: '${join(s, 'd')}' is not a file`,
    ],
    [
      'dir of a file',
      (c: any, s: string) => c.output.dir(join(s, 'f')),
      (s: string) => `ctx.output.dir: '${join(s, 'f')}' is not a directory`,
    ],
    [
      'same name twice',
      async (c: any) => {
        await c.output.write('x', '1');
        await c.output.write('x', '2');
      },
      "output file 'x' already exists",
    ],
    [
      'a file where a directory is',
      async (c: any) => {
        await c.output.write('a/b', '1');
        await c.output.write('a', '2');
      },
      "output file 'a' already exists",
    ],
    [
      'a dir ref claims <name>.tar',
      async (c: any, s: string) => {
        await c.output.dir(join(s, 'd'));
        await c.output.write('d.tar', '2');
      },
      "output file 'd.tar' already exists",
    ],
  ] as const)('output names are validated and unique: %s', async (_, call, message) => {
    const src = tmp('names');
    writeFileSync(join(src, 'f'), 'f');
    mkdirSync(join(src, 'd'));
    const impl = implement(producer, async (_i, ctx) => {
      await call(ctx, src);
      return null as any;
    });
    const r = (await testHandler(impl, {})) as any;
    expect(r.error).toEqual({
      kind: 'handler',
      message: `step 'producer' failed: ${typeof message === 'string' ? message : message(src)}`,
    });
  });

  test('fetch from a local pool dir (OLVE_FILES_DIR), from pre-signed URLs, and sha verification', async () => {
    const prod = await testHandler(
      implement(producer, async (_i, ctx) => ({
        a: await ctx.output.write('a.txt', 'aaa'),
        b: await ctx.output.write('b.txt', 'bbb'),
        d: await ctx.output.dir(EXAMPLE + '/scripts', { name: 'scripts' }),
      })),
      {},
      { run: { number: 3 } },
    );
    const files = (prod as any).output;
    // pool layout: <dir>/<run>/<step>/<attempt>/files/<object path>
    const pool = tmp('pool');
    mkdirSync(join(pool, '3/producer/1'), { recursive: true });
    await Bun.$`cp -r ${prod.filesDir} ${join(pool, '3/producer/1/files')}`;
    const reader = implement(consumer, async ({ files }, ctx) => {
      const a = await ctx.files.fetch(files.a);
      const d = await ctx.files.fetch(files.d);
      return {
        text:
          readFileSync(a, 'utf8') +
          '|' +
          readFileSync(join(d, 'deploy.sh'), 'utf8').split('\n')[0],
      };
    });
    const r1 = await withEnv({ OLVE_FILES_DIR: pool, OLVE_FILE_URLS: undefined }, () =>
      testHandler(reader, { files }, { run: { number: 4 } }),
    );
    expect(r1).toMatchObject({ status: 'succeeded', output: { text: 'aaa|#!/bin/sh' } });

    // pre-signed URL map (what the service provides): key = <run>/<step>/<attempt>/<path>
    const seen: string[] = [];
    const server = Bun.serve({
      port: 0,
      fetch(req) {
        const u = new URL(req.url);
        seen.push(u.pathname + u.search);
        const obj = u.pathname.slice(1);
        return new Response(Bun.file(join(prod.filesDir, obj)));
      },
    });
    servers.push(server);
    const base = `http://localhost:${server.port}`;
    const urls = {
      '3/producer/1/a.txt': `${base}/a.txt?X-Amz-Signature=s`,
      '3/producer/1/scripts/': `${base}/scripts.tar?X-Amz-Signature=s`,
    };
    const r2 = await withEnv(
      { OLVE_FILES_DIR: undefined, OLVE_FILE_URLS: JSON.stringify(urls) },
      () => testHandler(reader, { files }),
    );
    expect(r2).toMatchObject({ status: 'succeeded', output: { text: 'aaa|#!/bin/sh' } });
    expect(seen).toEqual(['/a.txt?X-Amz-Signature=s', '/scripts.tar?X-Amz-Signature=s']);

    // base URL + token
    const auth: (string | null)[] = [];
    const s2 = Bun.serve({
      port: 0,
      fetch(req) {
        auth.push(req.headers.get('authorization'));
        const obj = new URL(req.url).pathname.split('/').slice(4).join('/');
        return new Response(Bun.file(join(prod.filesDir, obj)));
      },
    });
    servers.push(s2);
    const r3 = await withEnv(
      {
        OLVE_FILE_URLS: undefined,
        OLVE_FILES_BASE_URL: `http://localhost:${s2.port}/`,
        OLVE_FILES_TOKEN: 'tok',
      },
      () => testHandler(reader, { files }),
    );
    expect(r3.status).toBe('succeeded');
    expect(auth).toEqual(['Bearer tok', 'Bearer tok']);

    // tampered content → sha mismatch
    const wrong = { ...files, a: { ...files.a, sha256: '0'.repeat(64) } };
    const r4 = await withEnv({ OLVE_FILES_DIR: pool }, () =>
      testHandler(reader, { files: wrong }),
    );
    expect((r4 as any).error.message).toMatch(/has sha256 .* expected 0000/);
    // no source at all
    const r5 = await withEnv(
      {
        OLVE_FILES_DIR: undefined,
        OLVE_FILE_URLS: undefined,
        OLVE_FILES_BASE_URL: undefined,
      },
      () => testHandler(reader, { files }),
    );
    expect((r5 as any).error.message).toMatch(/no source for 3\/producer\/1\/a.txt/);
  });
  test('refs are validated before use (no traversal)', async () => {
    const reader = implement(consumer, async ({ files }, ctx) => ({
      text: await ctx.files.fetch(files.a),
    }));
    const evil = testFileRef({ step: 'producer', path: '../../etc/passwd' });
    const r = await testHandler(reader, { files: { a: evil, b: evil, d: evil } });
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'input' }, exitCode: 65 });
    const evilStep = testFileRef({ step: '../..', path: 'a.txt' });
    const r2 = await testHandler(reader, {
      files: { a: evilStep, b: evilStep, d: evilStep },
    });
    expect(r2).toMatchObject({
      status: 'failed',
      error: { kind: 'input' },
      exitCode: 65,
    });
  });
  test.each([
    ['a path with ..', { path: '../../etc/passwd' }],
    ['a step name with ..', { step: '../..' }],
  ])('fetch checks a ref that skipped input validation: %s', async (_, bad) => {
    const ref = {
      run: 1,
      step: 'producer',
      attempt: 1,
      path: 'a.txt',
      sha256: '0'.repeat(64),
      ...bad,
    };
    const pool = tmp('evil-pool');
    const { fetch } = fetcher();
    const message = await withEnv({ ...FILE_ENV, OLVE_FILES_DIR: pool }, () =>
      messageOf(fetch(ref as any)),
    );
    expect(message).toBe(
      `ctx.files.fetch: not a valid file reference: ${JSON.stringify(ref)}`,
    );
  });
  test('a ref of the running attempt is read from its own output first; any other ref never is', async () => {
    const pool = tmp('own-pool');
    const foreign = [
      refTo('pool', { run: 4, attempt: 2 }),
      refTo('pool', { step: 'other', attempt: 2 }),
      refTo('pool', { attempt: 1 }),
    ];
    for (const r of foreign) {
      const at = join(pool, String(r.run), r.step, String(r.attempt), 'files');
      mkdirSync(at, { recursive: true });
      writeFileSync(join(at, 'a.txt'), 'pool');
    }
    const { fetch, deps } = fetcher();
    const own = await createOutput(deps).write('a.txt', 'own');
    await withEnv(FILE_ENV, async () =>
      expect(readFileSync(await fetch(own), 'utf8')).toBe('own'),
    );
    await withEnv({ ...FILE_ENV, OLVE_FILES_DIR: pool }, async () => {
      for (const r of foreign) expect(readFileSync(await fetch(r), 'utf8')).toBe('pool');
    });
  });
  test.each([
    ['[]', '(not an object)'],
    ['null', '(not an object)'],
    ['"x"', '(not an object)'],
    ['{', '(JSON Parse error'],
  ])('OLVE_FILE_URLS %s is refused', async (raw, reason) => {
    const { fetch } = fetcher();
    const message = await withEnv({ ...FILE_ENV, OLVE_FILE_URLS: raw }, () =>
      messageOf(fetch(refTo('a'))),
    );
    expect(message).toStartWith(
      `OLVE_FILE_URLS / files.json is not a JSON object of file key → URL ${reason}`,
    );
  });
  test('without OLVE_FILE_URLS the URL map is read from $OLVE_DIR/files.json', async () => {
    const server = Bun.serve({ port: 0, fetch: () => new Response('aaa') });
    servers.push(server);
    const { fetch, root } = fetcher();
    writeFileSync(
      join(root, 'files.json'),
      JSON.stringify({ '3/producer/1/a.txt': `http://localhost:${server.port}/a.txt` }),
    );
    const got = await withEnv(FILE_ENV, () => fetch(refTo('aaa')));
    expect(readFileSync(got, 'utf8')).toBe('aaa');
  });
  // [answers in order (0 = connection refused), outcome, backoff delays]
  test.each([
    ['404 fails at once', [404], 'HTTP 404', []],
    ['500 is retried', [500, 200], null, [1000]],
    ['429 is retried', [429, 200], null, [1000]],
    ['408 is retried', [408, 200], null, [1000]],
    [
      'a persistent 503 gives up after 4 tries',
      [503, 503, 503, 503],
      'HTTP 503',
      [1000, 2000, 4000],
    ],
  ] as const)('download: %s', async (name, answers, failure, delays) => {
    const queue = [...answers];
    let requests = 0;
    const server = Bun.serve({
      port: 0,
      fetch: () => {
        requests++;
        const status = queue.shift() ?? 500;
        return new Response(status === 200 ? 'aaa' : 'no', { status });
      },
    });
    servers.push(server);
    const url = `http://localhost:${server.port}/a.txt`;
    const f = fetcher();
    const urls = JSON.stringify({ '3/producer/1/a.txt': `${url}?X-Amz-Signature=s` });
    const result = await withEnv({ ...FILE_ENV, OLVE_FILE_URLS: urls }, () =>
      messageOf(f.fetch(refTo('aaa')).then((p) => readFileSync(p, 'utf8'))),
    );
    expect(result).toBe(failure ? `download of ${url}?… failed: ${failure}` : 'resolved');
    expect(requests).toBe(answers.length);
    expect(f.delays).toEqual([...delays]);
    expect(f.logs).toEqual(
      answers
        .slice(0, delays.length)
        .map(
          (s, i) =>
            `download failed, retrying url=${url}?… attempt=${i + 1} error="HTTP ${s}"`,
        ),
    );
  });
  test('download: a refused connection is retried', async () => {
    const closed = Bun.serve({ port: 0, fetch: () => new Response('') });
    const url = `http://localhost:${closed.port}/a.txt`;
    closed.stop(true);
    const f = fetcher();
    const message = await withEnv(
      { ...FILE_ENV, OLVE_FILE_URLS: JSON.stringify({ '3/producer/1/a.txt': url }) },
      () => messageOf(f.fetch(refTo('aaa'))),
    );
    expect(message).toStartWith(`download of ${url} failed: `);
    expect(f.delays).toEqual([1000, 2000, 4000]);
  });
  const symlinksIn = (dir: string): string[] =>
    readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
      e.isSymbolicLink()
        ? [join(dir, e.name)]
        : e.isDirectory()
          ? symlinksIn(join(dir, e.name))
          : [],
    );
  test('extracting a directory ref never writes outside its destination', async () => {
    const area = tmp('contain');
    const outside = join(area, 'outside');
    mkdirSync(outside);
    const payload = join(area, 'payload');
    writeFileSync(payload, 'pwned');
    const evil = (path: string): TarEntry => ({
      path,
      type: 'file',
      mode: 0o644,
      source: payload,
    });
    // The pool tar is what the producing step uploaded, so its sha256 matches: only extraction stands in the way.
    const rows: [string, TarEntry[]][] = [
      ['dotdot', [evil('../../../../../../../esc-dotdot')]],
      ['nested dotdot', [evil('a/../../../../../../../../esc-nested')]],
      ['absolute', [evil(join(outside, 'esc-abs'))]],
      [
        'symlink out, then a file through it',
        [{ path: 'l', type: 'symlink', target: outside }, evil('l/esc-via-link')],
      ],
      [
        'relative symlink out, then a file through it',
        [
          { path: 'r', type: 'symlink', target: '../../../../../../..' },
          evil('r/esc-via-rel'),
        ],
      ],
      [
        'a dangling symlink out',
        [{ path: 's', type: 'symlink', target: join(outside, 'esc-later') }],
      ],
    ];
    const pool = join(area, 'pool');
    const files = join(pool, '1/producer/1/files');
    mkdirSync(files, { recursive: true });
    const { fetch } = fetcher(join(area, 'olve'));
    for (const [name, entries] of rows) {
      const tar = join(files, `${name.replace(/\W/g, '-')}.tar`);
      writeTar(tar, entries);
      const ref = testFileRef({ step: 'producer', path: `${name.replace(/\W/g, '-')}/` });
      const dest = await withEnv({ ...FILE_ENV, OLVE_FILES_DIR: pool }, () =>
        fetch({ ...ref, sha256: sha256File(tar) }),
      );
      // A handler may later write through any symlink left behind: do that too.
      for (const p of symlinksIn(dest))
        for (const target of [join(p, 'esc-through'), p])
          try {
            writeFileSync(target, 'x');
          } catch {
            /* not writable that way */
          }
    }
    expect(readdirSync(outside)).toEqual([]);
    const escaped = (readdirSync(area, { recursive: true }) as string[])
      .filter((p) => p.split('/').pop()!.startsWith('esc-'))
      .filter((p) => !p.startsWith('olve/tmp/files/1/producer/1/'));
    expect(escaped).toEqual([]);
  });
  test('an output ref claiming this attempt must point to a produced file', async () => {
    const liar = implement(producer, async () => ({
      a: testFileRef({ step: 'producer', path: 'missing.txt' }),
      b: testFileRef({ step: 'other', path: 'ok.txt' }),
      d: testFileRef({ step: 'other', path: 'd/' }),
    }));
    const r = await testHandler(liar, {});
    expect(r).toMatchObject({
      status: 'failed',
      error: { kind: 'output' },
      exitCode: 66,
    });
    expect((r as any).error.message).toMatch(/no such output file/);
  });
});

describe('ctx.http and ctx.retry', () => {
  test('getJson, expectOk with a readable error, retry with backoff', async () => {
    let hits = 0;
    const server = Bun.serve({
      port: 0,
      fetch(req) {
        const p = new URL(req.url).pathname;
        if (p === '/json')
          return req.headers.get('accept') === 'application/json'
            ? Response.json({ ok: true })
            : new Response('json only', { status: 406 });
        if (p === '/echo') return new Response(`${req.method} ${req.headers.get('x-a')}`);
        if (p === '/empty') return new Response(null, { status: 500 });
        if (p === '/long') return new Response('z'.repeat(600), { status: 502 });
        if (p === '/flaky')
          return ++hits < 3 ? new Response('busy', { status: 503 }) : new Response('ok');
        return new Response('nope', { status: 404 });
      },
    });
    servers.push(server);
    const base = `http://localhost:${server.port}`;
    const impl = implement(plain, async (_i, ctx) => {
      const j = await ctx.http.getJson<{ ok: boolean }>(`${base}/json`);
      const echo = await (
        await ctx.http.expectOk(`${base}/echo`, {
          method: 'POST',
          headers: { 'x-a': '1' },
        })
      ).text();
      const errors = await Promise.all([
        messageOf(ctx.http.expectOk(`${base}/missing?token=secret`)),
        messageOf(ctx.http.getJson(`${base}/missing`)),
        messageOf(ctx.http.expectOk(`${base}/empty`)),
        messageOf(ctx.http.expectOk(`${base}/long`)),
      ]);
      await ctx.retry({ attempts: 5, delaySeconds: 0.01, backoff: 2 }, () =>
        ctx.http.expectOk(`${base}/flaky`),
      );
      return { text: JSON.stringify([j.ok, echo, ...errors]) };
    });
    const r = await testHandler(impl, { cmd: '' });
    expect(r).toMatchObject({ status: 'succeeded' });
    expect(JSON.parse((r as any).output.text)).toEqual([
      true,
      'POST 1',
      `HTTP 404 Not Found from ${base}/missing?…: nope`,
      `HTTP 404 Not Found from ${base}/missing: nope`,
      `HTTP 500 Internal Server Error from ${base}/empty`,
      `HTTP 502 Bad Gateway from ${base}/long: ${'z'.repeat(500)}`,
    ]);
    expect(hits).toBe(3);
    expect(r.logs.filter((l) => l.startsWith('retrying'))).toEqual([
      `retrying attempt=1 of=5 error="HTTP 503 Service Unavailable from ${base}/flaky: busy"`,
      `retrying attempt=2 of=5 error="HTTP 503 Service Unavailable from ${base}/flaky: busy"`,
    ]);
  });
  test.each([
    [{ delaySeconds: 0.01, backoff: 2 }, [10, 20, 40]],
    [{ delaySeconds: 0.5 }, [500, 500, 500]],
    [{ delaySeconds: -1, backoff: 2 }, [0, 0, 0]],
  ])('retry waits delaySeconds × backoff between attempts: %o', async (opts, delays) => {
    const waited: number[] = [];
    const retry = createRetry(
      new AbortController().signal,
      () => {},
      async (ms) => {
        waited.push(ms);
      },
    );
    let calls = 0;
    const p = retry({ attempts: 4, ...opts }, async () => {
      throw new Error(`fail ${++calls}`);
    });
    expect(await messageOf(p)).toBe('fail 4');
    expect(waited).toEqual(delays);
  });
  test.each([0, -1, 1.5])('retry refuses attempts: %p', async (attempts) => {
    let calls = 0;
    const retry = createRetry(new AbortController().signal, () => {});
    expect(
      await messageOf(retry({ attempts, delaySeconds: 0 }, async () => calls++)),
    ).toBe('retry: attempts must be a positive integer');
    expect(calls).toBe(0);
  });
  test('retry with attempts: 1 calls once and rethrows', async () => {
    let calls = 0;
    const retry = createRetry(new AbortController().signal, () => {});
    const p = retry({ attempts: 1, delaySeconds: 0 }, async () => {
      calls++;
      throw new Error('once');
    });
    expect([await messageOf(p), calls]).toEqual(['once', 1]);
  });
  test.each([
    ['m', undefined, 'm'],
    ['m', {}, 'm'],
    [
      'm',
      { a: 'x', b: 'a b', c: [1, 2], d: 'say:"hi"', e: 3, f: null },
      'm a=x b="a b" c=[1,2] d="say:\\"hi\\"" e=3 f=null',
    ],
  ])('formatLog(%p, %o)', (message, fields, line) => {
    expect(formatLog(message, fields)).toBe(line);
  });
});

describe('writeTar', () => {
  test('a file that shrinks while it is archived fails instead of looping', () => {
    const dir = tmp('shrink');
    writeFileSync(join(dir, 'empty'), '');
    // lstat sizes the link (its target's length); reading follows it to 0 bytes.
    symlinkSync(join(dir, 'empty'), join(dir, 'src'));
    expect(() =>
      writeTar(join(dir, 'out.tar'), [
        { path: 'f', type: 'file', mode: 0o644, source: join(dir, 'src') },
      ]),
    ).toThrow(new Error(`${join(dir, 'src')} shrank while archiving`));
  });
});

describe('testHandler on the example handlers', () => {
  test('checkout → build → deploy-beta chain through file refs, run info, skip', async () => {
    const co = await testHandler(
      checkoutImpl,
      { repo: 'OliverVea/Olve.Pipelines', sha: SHA },
      { run: { number: 412, version: '2026.10.21.412' } },
    );
    expect(co).toMatchObject({
      status: 'succeeded',
      output: {
        repo: 'OliverVea/Olve.Pipelines',
        sha: SHA,
        tree: { run: 412, step: 'checkout', attempt: 1, path: 'tree/' },
      },
    });
    expect(co.logs[0]).toMatch(/^checked out repo=OliverVea\/Olve.Pipelines/);
    const source = (co as any).output;
    const b = await testHandler(
      buildImpl,
      { source },
      {
        run: { number: 412, version: '2026.10.21.412' },
        ctx: { files: { fetch: async () => join(EXAMPLE, 'steps') } },
      },
    );
    expect(b).toMatchObject({
      status: 'succeeded',
      output: {
        version: '2026.10.21.412+0123456',
        image: { path: 'image.tar' },
        chart: { path: 'chart/' },
      },
    });
    const pool = tmp('chain');
    mkdirSync(join(pool, '412/build/1'), { recursive: true });
    await Bun.$`cp -r ${b.filesDir} ${join(pool, '412/build/1/files')}`;
    const artifact = (b as any).output;
    const beta = await withEnv({ OLVE_FILES_DIR: pool }, () =>
      testHandler(deployBetaImpl, { artifact }, { secrets: { SSH_PRIVATE_KEY: 'key' } }),
    );
    expect(beta).toMatchObject({
      status: 'succeeded',
      output: { release: 'olve-pipelines-beta', version: artifact.version },
    });
    const skipped = await withEnv({ EXAMPLE_SKIP_BETA: '1' }, () =>
      testHandler(deployBetaImpl, { artifact }),
    );
    expect(skipped).toMatchObject({
      status: 'skipped',
      reason: 'EXAMPLE_SKIP_BETA is set',
      exitCode: 0,
    });
    const badUrl = await withEnv(
      { EXAMPLE_BETA_URL: 'not a url', OLVE_FILES_DIR: pool },
      () =>
        testHandler(deployBetaImpl, { artifact }, { secrets: { SSH_PRIVATE_KEY: 'k' } }),
    );
    expect(badUrl).toMatchObject({
      status: 'failed',
      error: { kind: 'output' },
      exitCode: 66,
    });
  });
  test('a handler running an embedded script; a step that carries its implementation', async () => {
    const r = await testHandler(testImpl, { source: testFileRef as any } as any);
    expect(r).toMatchObject({ status: 'failed', error: { kind: 'input' } });
    const source = {
      repo: 'r',
      sha: SHA,
      tree: testFileRef({ step: 'checkout', path: 'tree/' }),
    };
    expect(await testHandler(testImpl, { source })).toMatchObject({
      status: 'succeeded',
      output: { passed: 3, failed: 0 },
    });
    const Tag = model('Tag', z.object({ tag: z.string() }));
    const tagStep = (prefix: string) =>
      implement(
        step({
          name: 'tag',
          description: '',
          kind: 'tag',
          image: 'alpine',
          input: { deployTo: z.enum(['beta', 'prod']) },
          output: Tag,
          settings: { prefix },
        }),
        ({ deployTo }, ctx) => ({
          tag: `${prefix}-${deployTo}.${ctx.run.number}`,
        }),
      );
    expect(tagStep('app').settings).toEqual({ prefix: 'app' });
    expect(
      await testHandler(tagStep('app'), { deployTo: 'prod' }, { run: { number: 9 } }),
    ).toMatchObject({ status: 'succeeded', output: { tag: 'app-prod.9' } });
  });
  test('input is validated like input.json (unknown keys, wrong types)', async () => {
    const r = await testHandler(checkoutImpl, { repo: 'r', sha: SHA, extra: 1 } as any);
    expect(r).toMatchObject({
      status: 'failed',
      error: {
        kind: 'input',
        message: expect.stringMatching(/input.extra: not an input/),
      },
      exitCode: 65,
    });
  });
  test('Source is the library source model', () => expect(Source.name).toBe('Source'));
});

describe('ctx.link', () => {
  const Out = model('LinkOut', z.object({ ok: z.boolean() }));
  const linking = (fn: (ctx: any) => unknown) =>
    implement(
      step({ name: 'linker', description: '', image: 'alpine', output: Out }),
      (async (_: unknown, ctx: any) => fn(ctx)) as never,
    );

  test.each([
    {
      name: 'succeeded, with and without a label',
      fn: (ctx: any) => {
        ctx.link('https://example.com/app', 'App');
        ctx.link('http://beta.example.com');
        return { ok: true };
      },
      status: 'succeeded',
      links: [
        { url: 'https://example.com/app', label: 'App' },
        { url: 'http://beta.example.com' },
      ],
    },
    {
      name: 'skipped keeps its links',
      fn: (ctx: any) => {
        ctx.link('https://example.com');
        return ctx.skip('already deployed');
      },
      status: 'skipped',
      links: [{ url: 'https://example.com' }],
    },
    {
      name: 'a handler failure keeps its links',
      fn: (ctx: any) => {
        ctx.link('https://example.com');
        throw new Error('boom');
      },
      status: 'failed',
      links: [{ url: 'https://example.com' }],
    },
    {
      name: 'an output failure keeps its links',
      fn: (ctx: any) => {
        ctx.link('https://example.com');
        return { ok: 'no' };
      },
      status: 'failed',
      links: [{ url: 'https://example.com' }],
    },
    {
      name: 'no links: no links key',
      fn: () => ({ ok: true }),
      status: 'succeeded',
      links: undefined,
    },
  ])('$name', async ({ fn, status, links }) => {
    const r = await testHandler(linking(fn) as any, {});
    expect(r.status).toBe(status as never);
    expect(r.links).toEqual(links as never);
    if (!links) expect('links' in r).toBe(false);
  });

  test.each([
    [
      'example.com',
      undefined,
      "ctx.link: 'example.com' is not an absolute http or https URL",
    ],
    [
      'ftp://example.com',
      undefined,
      "ctx.link: 'ftp://example.com' is not an absolute http or https URL",
    ],
    ['https://', undefined, "ctx.link: 'https://' is not an absolute http or https URL"],
    [
      `https://example.com/${'a'.repeat(2048)}`,
      undefined,
      'ctx.link: the URL is longer than 2048 characters',
    ],
    [
      'https://example.com',
      'x'.repeat(101),
      'ctx.link: the label is longer than 100 characters',
    ],
  ])('refuses %p (label %p)', async (url, label, message) => {
    const r = await testHandler(
      linking((ctx) => {
        ctx.link(url, label);
        return { ok: true };
      }) as any,
      {},
    );
    expect(r.status).toBe('failed');
    expect((r as any).error).toEqual({
      kind: 'handler',
      message: `step 'linker' failed: ${message}`,
    });
  });

  test('at most 10 links', async () => {
    const r = await testHandler(
      linking((ctx) => {
        for (let i = 0; i <= 10; i++) ctx.link(`https://example.com/${i}`);
        return { ok: true };
      }) as any,
      {},
    );
    expect((r as any).error.message).toBe(
      "step 'linker' failed: ctx.link: a step can add at most 10 links",
    );
    expect(r.links).toHaveLength(10);
  });
});
