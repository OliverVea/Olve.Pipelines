import { mkdirSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import * as M from '../models';

export const buildSdk = implement(
  step({
    name: 'build-sdk',
    description:
      'Checks src/sdk/ (tsc, compile-fail cases, bun test), runs the pipeline and deployer tests, and packs the SDK.',
    image: 'oven/bun:1.4.3-slim',
    input: { source: Source },
    output: M.SdkPackage,
    timeoutSeconds: 1200,
  }),
  async ({ source }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    const dir = join(tree, 'src/sdk');
    const { name, version } = await ctx.readJson<{ name: string; version: string }>(
      join(dir, 'package.json'),
    );

    // Its own HOME, for bun's cache.
    const home = join(ctx.tmpDir, 'home');
    mkdirSync(home, { recursive: true });
    const env = { HOME: home };
    await ctx.exec(['bun', 'install', '--frozen-lockfile'], { cwd: dir, env });
    await ctx.exec(['bun', 'run', 'check'], { cwd: dir, env });

    const pipelines = join(tree, '.pipelines');
    await ctx.exec(['bun', 'install', '--frozen-lockfile'], { cwd: pipelines, env });
    await ctx.exec(['bun', 'test'], { cwd: pipelines, env });
    await ctx.exec(['bun', 'test'], {
      cwd: join(tree, 'src/deploy/compose/deployer'),
      env,
    });

    const out = join(ctx.tmpDir, 'pack');
    mkdirSync(out, { recursive: true });
    await ctx.exec(['bun', 'pm', 'pack', '--destination', out], { cwd: dir, env });
    const packed = readdirSync(out).filter((f) => f.endsWith('.tgz'));
    if (packed.length !== 1)
      throw new Error(`bun pm pack wrote ${JSON.stringify(packed)}, expected one .tgz`);
    const tarball = await ctx.output.file(join(out, packed[0]!), { name: 'package.tgz' });
    ctx.log('SDK checked and packed', { name, version });
    return { name, version, tarball };
  },
);
