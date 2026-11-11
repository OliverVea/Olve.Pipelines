import { cpSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { type Ctx, type Infer, implement, step, Source } from '@olivervea/pipelines';
import * as M from '../models';

/** The released file name of each platform's pl. */
export const CLI_FILES = {
  'linux-x64': 'pl-linux-x64',
  'windows-x64': 'pl-win-x64.exe',
} as const;

/** src/cli/ with its dependencies installed, and the options for running commands in it. */
async function cliTree(
  ctx: Ctx,
  source: Infer<typeof Source>,
  spec: Infer<typeof M.SpecBuild>,
) {
  const tree = await ctx.files.fetch(source.tree);
  cpSync(await ctx.files.fetch(spec.artifacts), join(tree, 'artifacts'), {
    recursive: true,
  });
  const dir = join(tree, 'src/cli');
  // Its own HOME, for bun's cache.
  const home = join(ctx.tmpDir, 'home');
  mkdirSync(home, { recursive: true });
  const opts = { cwd: dir, env: { HOME: home } };
  await ctx.exec(['bun', 'install', '--frozen-lockfile'], opts);
  return opts;
}

export const checkCli = implement(
  step({
    name: 'check-cli',
    description: 'Checks src/cli/: prettier, tsc and bun test.',
    image: 'oven/bun:1.4.3-slim',
    input: { source: Source, spec: M.SpecBuild },
    timeoutSeconds: 900,
  }),
  async ({ source, spec }, ctx) => {
    const opts = await cliTree(ctx, source, spec);
    await ctx.exec(['bun', 'run', 'format:check'], opts);
    await ctx.exec(['bun', 'run', 'typecheck'], opts);
    await ctx.exec(['bun', 'test'], opts);
  },
);

export const buildCli = implement(
  step({
    name: 'build-cli',
    description: 'Compiles pl for one platform.',
    image: 'oven/bun:1.4.3-slim',
    input: { source: Source, spec: M.SpecBuild },
    output: M.CliBinary,
    timeoutSeconds: 900,
    matrix: { axes: { os: ['linux', 'windows'], arch: ['x64'] } },
  }),
  async ({ source, spec }, ctx) => {
    const opts = await cliTree(ctx, source, spec);
    const { os, arch } = ctx.cell;
    const version = ctx.run.version;
    const name = CLI_FILES[`${os}-${arch}`];
    const path = join(ctx.tmpDir, 'cli', name);
    await ctx.exec(
      [
        'bun',
        'build',
        'src/main.ts',
        '--compile',
        '--minify',
        `--target=bun-${os}-${arch}`,
        '--define',
        `process.env.PL_VERSION=${JSON.stringify(version)}`,
        '--outfile',
        path,
      ],
      opts,
    );
    if (os === 'linux') await ctx.exec([path, '--version']);
    ctx.log('built pl', { version, name });
    return { version, binary: await ctx.output.file(path, { name }) };
  },
);
