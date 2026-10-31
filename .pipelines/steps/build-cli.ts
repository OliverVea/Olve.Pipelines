import { cpSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import * as M from '../models';

const TARGETS = [
  { target: 'bun-linux-x64', name: 'pl-linux-x64' },
  { target: 'bun-windows-x64', name: 'pl-win-x64.exe' },
] as const;

export const buildCli = implement(
  step({
    name: 'build-cli',
    description:
      'Checks src/cli/ (prettier, tsc, bun test) and compiles pl for Linux and Windows.',
    image: 'oven/bun:1.4.3-slim',
    input: { source: Source, spec: M.SpecBuild },
    output: M.CliBuild,
    timeoutSeconds: 900,
  }),
  async ({ source, spec }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    cpSync(await ctx.files.fetch(spec.artifacts), join(tree, 'artifacts'), {
      recursive: true,
    });
    const dir = join(tree, 'src/cli');
    const version = ctx.run.version;

    // Clean env for the checks: the harness's OLVE_* variables would leak into pl's own tests.
    const home = join(ctx.tmpDir, 'home');
    mkdirSync(home, { recursive: true });
    const clean = [
      'env',
      '-i',
      `PATH=${process.env.PATH ?? '/usr/local/bin:/usr/bin:/bin'}`,
      `HOME=${home}`,
      `TMPDIR=${ctx.tmpDir}`,
    ];
    await ctx.exec([...clean, 'bun', 'install', '--frozen-lockfile'], { cwd: dir });
    await ctx.exec([...clean, 'bun', 'run', 'format:check'], { cwd: dir });
    await ctx.exec([...clean, 'bun', 'run', 'typecheck'], { cwd: dir });
    await ctx.exec([...clean, 'bun', 'test'], { cwd: dir });

    const out = join(ctx.tmpDir, 'cli');
    for (const { target, name } of TARGETS)
      await ctx.exec(
        [
          ...clean,
          'bun',
          'build',
          'src/main.ts',
          '--compile',
          '--minify',
          `--target=${target}`,
          '--define',
          `process.env.PL_VERSION=${JSON.stringify(version)}`,
          '--outfile',
          join(out, name),
        ],
        { cwd: dir },
      );
    await ctx.exec([join(out, 'pl-linux-x64'), '--version']);

    const linuxX64 = await ctx.output.file(join(out, 'pl-linux-x64'), {
      name: 'pl-linux-x64',
    });
    const winX64 = await ctx.output.file(join(out, 'pl-win-x64.exe'), {
      name: 'pl-win-x64.exe',
    });
    ctx.log('checked and built pl', { version });
    return { version, linuxX64, winX64 };
  },
);
