// olve-build: handler-hash behaviour, determinism, the compiled binary end to end.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { beforeAll, describe, expect, test } from 'bun:test';
import {
  copyFileSync,
  cpSync,
  existsSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
import { build } from '../../src/build/build.ts';
import { readInterpreter, GLIBC_INTERP } from '../../src/build/elf.ts';
import { handlerHashes, kindHashes } from '../../src/build/hashes.ts';
import { model, pipeline, z } from '../../src/index.ts';
import { harnessInfo, loadProject } from '../../src/build/project.ts';
import { devMain } from '../../src/dev/cli.ts';
import { EXIT } from '../../src/harness/exit.ts';
import { EXAMPLE, SDK, SHA, tmp } from './helpers.ts';

function copyExample(dest: string): string {
  cpSync(EXAMPLE, dest, { recursive: true, filter: (src) => !src.includes('/.olve') });
  return dest;
}
const edit = (file: string, from: string | RegExp, to: string) => {
  const before = readFileSync(file, 'utf8');
  const after = before.replace(from, to);
  if (after === before) throw new Error(`edit did not apply to ${file}: ${from}`);
  writeFileSync(file, after);
};

describe('handler hashes (contract files and SDK external, function-level granularity)', () => {
  let baseline: Record<string, string>;
  const hashesOf = async (dir: string) =>
    Object.fromEntries(
      Object.entries(await handlerHashes(await loadProject(dir), harnessInfo())).map(
        ([k, v]) => [k, v.handlerHash],
      ),
    );
  const changed = async (fn: (dir: string) => void) => {
    const dir = copyExample(join(tmp('hash'), 'p'));
    fn(dir);
    const h = await hashesOf(dir);
    return Object.keys(h)
      .filter((k) => h[k] !== baseline[k])
      .sort();
  };
  beforeAll(async () => {
    baseline = await hashesOf(copyExample(join(tmp('hash'), 'p')));
  });

  test('no edits, different directory → identical', async () =>
    expect(await changed(() => {})).toEqual([]));
  test('edit a handler → only that handler', async () =>
    expect(
      await changed((d) =>
        edit(join(d, 'handlers/build.ts'), "'image.tar'", "'image2.tar'"),
      ),
    ).toEqual(['build']));
  test('edit a shared helper → only handlers that use that function', async () => {
    expect(
      await changed((d) =>
        edit(join(d, 'handlers/lib/release.ts'), 'slice(0, 7)', 'slice(0, 8)'),
      ),
    ).toEqual(['build']);
    expect(
      await changed((d) =>
        edit(
          join(d, 'handlers/lib/release.ts'),
          'olve-pipelines-${env}',
          'olve-pl-${env}',
        ),
      ),
    ).toEqual(['deploy-beta', 'deploy-prod']);
  });
  test('unused export / comment-only edit → nothing', async () => {
    expect(
      await changed((d) =>
        writeFileSync(
          join(d, 'handlers/lib/release.ts'),
          readFileSync(join(d, 'handlers/lib/release.ts'), 'utf8') +
            'export const unused = 1;\n',
        ),
      ),
    ).toEqual([]);
    expect(
      await changed((d) =>
        writeFileSync(
          join(d, 'handlers/build.ts'),
          readFileSync(join(d, 'handlers/build.ts'), 'utf8') + '// just a comment\n',
        ),
      ),
    ).toEqual([]);
  });
  test('edit an embedded script → the handlers embedding it', async () =>
    expect(
      await changed((d) =>
        writeFileSync(
          join(d, 'scripts/deploy.sh'),
          readFileSync(join(d, 'scripts/deploy.sh'), 'utf8') + '# changed\n',
        ),
      ),
    ).toEqual(['deploy-beta', 'deploy-prod']));
  test('edit contract files (pipeline.ts, models.ts) → no handler hash changes', async () => {
    expect(
      await changed((d) =>
        edit(
          join(d, 'pipeline.ts'),
          "image: 'alpine:latest',\n  input: { artifact: M.BuildArtifact },\n  output: M.BetaDeployment",
          "image: 'alpine:3.20',\n  input: { artifact: M.BuildArtifact },\n  output: M.BetaDeployment",
        ),
      ),
    ).toEqual([]);
    expect(
      await changed((d) =>
        edit(
          join(d, 'models.ts'),
          'failed: z.number().int() }',
          'failed: z.number().int(), skipped: z.number().optional() }',
        ),
      ),
    ).toEqual([]);
  });
});

describe('kind hashes (steps that carry their implementation)', () => {
  const TAG = `import { implement, model, step, z } from "@olivervea/pipelines";
export const Tag = model("Tag", z.object({ tag: z.string() }));
export const tagStep = (prefix: string) =>
  implement(step({ name: "tag", description: "", kind: "tag", image: "alpine", output: Tag, settings: { prefix } }), (_i, ctx) => ({ tag: \`\${prefix}-\${ctx.run.number}\` }));
`;
  const kindsOf = async (body: string, prefix = 'app') => {
    const dir = copyExample(join(tmp('kinds'), 'p'));
    mkdirSync(join(dir, 'steps'));
    writeFileSync(join(dir, 'steps/tag.ts'), body);
    const { tagStep } = (await import(join(dir, 'steps/tag.ts'))) as {
      tagStep: (p: string) => never;
    };
    const p = pipeline({
      name: 'k',
      input: model('KIn', z.object({ x: z.string() })),
    }).stage(tagStep(prefix));
    return kindHashes(await loadProject(dir), p);
  };
  test('one hash per kind, from the module that implements it; the settings stay out of it', async () => {
    const a = await kindsOf(TAG);
    expect(Object.keys(a)).toEqual(['tag']);
    expect(await kindsOf(TAG, 'other')).toEqual(a);
    expect(await kindsOf(TAG + '// a comment\n')).toEqual(a);
    expect(
      (await kindsOf(TAG.replace('ctx.run.number', 'ctx.run.attempt'))).tag,
    ).not.toBe(a.tag);
  });
});

describe('compiled binary', () => {
  let a: Awaited<ReturnType<typeof build>>;
  let dirA: string;
  beforeAll(async () => {
    dirA = copyExample(join(tmp('build-a'), 'pipeline'));
    a = await build(dirA, { out: join(dirA, '..', 'out'), log: () => {} });
  }, 60000);

  test('PT_INTERP is the glibc loader; step.tar.zst holds `step` (0755)', async () => {
    expect(readInterpreter(a.binary)).toBe(GLIBC_INTERP);
    const unpacked = `${a.archive}.tar`;
    writeFileSync(unpacked, Bun.zstdDecompressSync(await Bun.file(a.archive).bytes()));
    const tar = Bun.spawnSync(['tar', '-tvf', unpacked])
      .stdout.toString()
      .trim()
      .split('\n');
    expect(tar).toHaveLength(1);
    expect(tar[0]).toMatch(/^-rwxr-xr-x 0\/0 .* 1970-01-01 00:00 step$/);
  });
  test('the generated registry uses literal imports', () => {
    const reg = readFileSync(join(dirA, '.olve/gen/handlers.ts'), 'utf8');
    expect(reg).toContain(
      '"deploy-beta": () => import("../../handlers/deploy-beta.ts"),',
    );
  });
  test('deterministic: another directory gives the same manifest, binary and archive', async () => {
    const dirB = copyExample(join(tmp('build-b'), 'pipeline'));
    const b = await build(dirB, { out: join(dirB, '..', 'out'), log: () => {} });
    expect(b.manifest).toBe(a.manifest);
    expect(Bun.hash(readFileSync(b.binary))).toBe(Bun.hash(readFileSync(a.binary)));
    expect(Bun.hash(readFileSync(b.archive))).toBe(Bun.hash(readFileSync(a.archive)));
    // A different nesting depth changes embedded module paths (so the binary bytes), never the manifest.
    const dirC = copyExample(join(tmp('build-c'), 'deeper', 'pipeline'));
    const c = await build(dirC, { out: join(dirC, '..', 'out'), log: () => {} });
    expect(c.manifest).toBe(a.manifest);
  }, 60000);
  test("dev describe (from source) is byte-identical to the binary's describe", async () => {
    const proc = Bun.spawnSync([
      process.execPath,
      join(import.meta.dir, '../../src/dev/cli.ts'),
      'describe',
      dirA,
    ]);
    expect(proc.exitCode).toBe(0);
    expect(proc.stdout.toString()).toBe(a.manifest);
  });
  test('binary run: checkout writes the envelope and files; exit codes propagate', () => {
    const io = tmp('bin-run');
    writeFileSync(
      join(io, 'input.json'),
      JSON.stringify({ repo: 'OliverVea/Olve.Pipelines', sha: SHA }),
    );
    const env = {
      PATH: process.env.PATH!,
      OLVE_DIR: io,
      OLVE_RUN: '412',
      OLVE_ATTEMPT: '1',
      OLVE_VERSION_STAMP: '2026.10.21.412',
      TMPDIR: join(io, 'tmp'),
    };
    const p = Bun.spawnSync([a.binary, 'run', 'checkout'], { env, cwd: io });
    expect(p.exitCode).toBe(0);
    const envelope = JSON.parse(readFileSync(join(io, 'work/output.json'), 'utf8'));
    expect(envelope).toMatchObject({
      status: 'succeeded',
      output: { tree: { run: 412, step: 'checkout', path: 'tree/' } },
    });
    expect(p.stdout.toString()).toContain(
      '---OLVE-RESULT-BEGIN---\n' + JSON.stringify(envelope) + '\n---OLVE-RESULT-END---',
    );
    writeFileSync(join(io, 'input.json'), '{}');
    expect(Bun.spawnSync([a.binary, 'run', 'checkout'], { env, cwd: io }).exitCode).toBe(
      EXIT.inputInvalid,
    );
    expect(Bun.spawnSync([a.binary, 'run', 'nope'], { env, cwd: io }).exitCode).toBe(
      EXIT.usage,
    );
    // a .env / bunfig.toml in the cwd is not autoloaded by the compiled binary
    writeFileSync(join(io, '.env'), 'EXAMPLE_SKIP_BETA=1\n');
    writeFileSync(join(io, 'input.json'), JSON.stringify({ value: SHA }));
    const m = Bun.spawnSync([a.binary, 'map-trigger', 'poll'], { env, cwd: io });
    expect(m.exitCode).toBe(0);
    expect(
      JSON.parse(readFileSync(join(io, 'work/output.json'), 'utf8')).output.sha,
    ).toBe(SHA);
  });
  test('bytecode keeps mapper source: a ternary mapper fails describe in the compiled binary (78)', async () => {
    const dir = copyExample(join(tmp('tern'), 'pipeline'));
    edit(
      join(dir, 'pipeline.ts'),
      'sha: from.input.sha })])',
      'sha: from.input.publishCli ? from.input.sha : from.input.repo })])',
    );
    const err = await build(dir, { out: join(dir, '..', 'out'), log: () => {} }).then(
      () => undefined,
      (e) => e,
    );
    expect(err?.exitCode).toBe(EXIT.configInvalid);
    expect(String(err?.message)).toMatch(/step 'checkout': mapper uses a ternary/);
  }, 60000);
  test('missing handler file = build error (78)', async () => {
    const dir = copyExample(join(tmp('nohandler'), 'pipeline'));
    rmSync(join(dir, 'handlers/summary.ts'));
    const err = await build(dir, { out: join(dir, '..', 'out'), log: () => {} }).then(
      () => undefined,
      (e) => e,
    );
    expect(err?.exitCode).toBe(EXIT.configInvalid);
    expect(String(err?.message)).toMatch(/step 'summary' has no handler/);
  }, 60000);

  test('handlers pair by the step they implement, not by file name', async () => {
    const dir = copyExample(join(tmp('rename'), 'pipeline'));
    renameSync(join(dir, 'handlers/summary.ts'), join(dir, 'handlers/report.ts'));
    const project = await loadProject(dir);
    expect(project.handlers.find(([step]) => step === 'summary')?.[1]).toBe(
      join(dir, 'handlers/report.ts'),
    );
  });

  test('a top-level handler file must implement a step, and only one file per step', async () => {
    const dir = copyExample(join(tmp('badhandlers'), 'pipeline'));
    writeFileSync(join(dir, 'handlers/helpers.ts'), 'export const x = 1;\n');
    await expect(loadProject(dir)).rejects.toThrow(
      /handlers\/helpers.ts must default-export implement/,
    );
    rmSync(join(dir, 'handlers/helpers.ts'));
    copyFileSync(join(dir, 'handlers/summary.ts'), join(dir, 'handlers/summary-copy.ts'));
    await expect(loadProject(dir)).rejects.toThrow(/both implement step 'summary'/);
  });
});

describe('as an installed dependency (package exports, no tsconfig paths)', () => {
  test('bun install file:sdk → olve-build bin → same manifest as in-repo; binary runs', async () => {
    const root = mkdtempSync(join(tmpdir(), 'olve-consumer-'));
    try {
      writeFileSync(
        join(root, 'package.json'),
        JSON.stringify({
          name: 'consumer',
          private: true,
          type: 'module',
          dependencies: { '@olivervea/pipelines': `file:${SDK}` },
        }),
      );
      copyExample(join(root, '.pipelines'));
      const env = {
        ...process.env,
        PATH: `${dirname(process.execPath)}:${process.env.PATH}`,
      };
      const inst = Bun.spawnSync([process.execPath, 'install'], {
        cwd: root,
        env,
        stdout: 'pipe',
        stderr: 'pipe',
      });
      expect(inst.exitCode).toBe(0);
      const p = Bun.spawnSync(
        [
          join(root, 'node_modules/.bin/olve-build'),
          '.pipelines',
          '--out',
          'out',
          '--sentinels',
        ],
        { cwd: root, env, stdout: 'pipe', stderr: 'pipe' },
      );
      expect(p.stderr.toString()).toContain('wrote');
      expect(p.exitCode).toBe(0);
      const dev = Bun.spawnSync([
        process.execPath,
        join(SDK, 'src/dev/cli.ts'),
        'describe',
        EXAMPLE,
      ]);
      expect(readFileSync(join(root, 'out/manifest.json'), 'utf8')).toBe(
        dev.stdout.toString(),
      );
      expect(p.stdout.toString()).toBe(
        `---OLVE-MANIFEST-BEGIN---\n${dev.stdout.toString()}---OLVE-MANIFEST-END---\n`,
      );
      mkdirSync(join(root, 'io'));
      writeFileSync(join(root, 'io/input.json'), JSON.stringify({ repo: 'r', sha: SHA }));
      const run = Bun.spawnSync([join(root, 'out/step'), 'run', 'checkout'], {
        env: { PATH: env.PATH, OLVE_DIR: join(root, 'io') },
      });
      expect(run.exitCode).toBe(0);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }, 60000);
});

describe('cancellation through the real loader', () => {
  test('SIGTERM to `ld-linux --library-path … step run` aborts ctx.signal, kills children, exits 143', async () => {
    const dir = join(tmp('sigterm'), 'pipeline');
    mkdirSync(join(dir, 'handlers'), { recursive: true });
    writeFileSync(
      join(dir, 'pipeline.ts'),
      `import { model, pipeline, step, z } from "@olivervea/pipelines";
export const Req = model("Req", z.object({ x: z.string() }));
export const slow = step({ name: "slow", description: "", image: "alpine" });
export default pipeline({ name: "slow", input: Req }).stage(slow);
`,
    );
    writeFileSync(
      join(dir, 'handlers/slow.ts'),
      `import { implement } from "@olivervea/pipelines";
import { slow } from "../pipeline";
export default implement(slow, async (_i, ctx) => { await ctx.sh("sleep 30"); });
`,
    );
    const b = await build(dir, { out: join(dir, '..', 'out'), log: () => {} });
    const io = tmp('sigterm-io');
    writeFileSync(join(io, 'input.json'), '{}');
    const loader = '/lib64/ld-linux-x86-64.so.2';
    const proc = Bun.spawn(
      [loader, '--library-path', '/lib/x86_64-linux-gnu', b.binary, 'run', 'slow'],
      {
        env: { PATH: process.env.PATH!, OLVE_DIR: io, TMPDIR: join(io, 'tmp') },
        stdout: 'pipe',
        stderr: 'pipe',
      },
    );
    await Bun.sleep(500);
    const t0 = Date.now();
    proc.kill('SIGTERM');
    expect(await proc.exited).toBe(EXIT.sigterm);
    expect(Date.now() - t0).toBeLessThan(5000);
    expect(JSON.parse(readFileSync(join(io, 'work/output.json'), 'utf8'))).toMatchObject({
      status: 'failed',
      error: { kind: 'handler', message: expect.stringMatching(/cancelled \(SIGTERM\)/) },
    });
  }, 60000);
});

describe('olve-dev run', () => {
  test('runs a step from source into <dir>/.olve/work', async () => {
    const dir = copyExample(join(tmp('dev'), 'pipeline'));
    const input = join(dir, '..', 'in.json');
    writeFileSync(input, JSON.stringify({ repo: 'r', sha: SHA }));
    mkdirSync(join(dir, '.olve'), { recursive: true });
    const p = Bun.spawnSync([
      process.execPath,
      join(import.meta.dir, '../../src/dev/cli.ts'),
      'run',
      dir,
      'checkout',
      '--input-file',
      input,
      '--secret',
      'GITHUB_TOKEN=x',
    ]);
    expect(p.exitCode).toBe(0);
    expect(
      JSON.parse(readFileSync(join(dir, '.olve/work/output.json'), 'utf8')).status,
    ).toBe('succeeded');
    // a second run in the same $OLVE_DIR starts from clean work/ (no "already exists", no stale files)
    writeFileSync(join(dir, '.olve/work/files/stale.txt'), 'old');
    const again = Bun.spawnSync([
      process.execPath,
      join(import.meta.dir, '../../src/dev/cli.ts'),
      'run',
      dir,
      'checkout',
      '--input-file',
      input,
    ]);
    expect(again.exitCode).toBe(0);
    expect(existsSync(join(dir, '.olve/work/files/stale.txt'))).toBe(false);
    expect(await devMain(['run', dir])).toBe(EXIT.usage);
  });

  test('map-trigger maps an event file; without one it is a usage error', async () => {
    const dir = copyExample(join(tmp('dev'), 'pipeline'));
    const event = join(dir, '..', 'event.json');
    writeFileSync(
      event,
      JSON.stringify({
        ref: 'refs/heads/main',
        after: SHA,
        repository: { full_name: 'o/r' },
      }),
    );
    const p = Bun.spawnSync([
      process.execPath,
      join(import.meta.dir, '../../src/dev/cli.ts'),
      'map-trigger',
      dir,
      'push',
      '--event-file',
      event,
    ]);
    expect(p.exitCode).toBe(0);
    expect(
      JSON.parse(readFileSync(join(dir, '.olve/work/output.json'), 'utf8')),
    ).toMatchObject({ status: 'succeeded', output: { repo: 'o/r', sha: SHA } });
    expect(await devMain(['map-trigger', dir, 'push'])).toBe(EXIT.usage);
  });
});
