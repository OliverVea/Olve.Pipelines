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

    // Clean env for the checks: the harness's OLVE_* variables would leak into the SDK's own tests.
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
    await ctx.exec([...clean, 'bun', 'run', 'check'], { cwd: dir });

    const pipelines = join(tree, '.pipelines');
    await ctx.exec([...clean, 'bun', 'install', '--frozen-lockfile'], { cwd: pipelines });
    await ctx.exec([...clean, 'bun', 'test'], { cwd: pipelines });
    await ctx.exec([...clean, 'bun', 'test'], {
      cwd: join(tree, 'src/deploy/compose/deployer'),
    });

    const out = join(ctx.tmpDir, 'pack');
    mkdirSync(out, { recursive: true });
    await ctx.exec([...clean, 'bun', 'pm', 'pack', '--destination', out], { cwd: dir });
    const packed = readdirSync(out).filter((f) => f.endsWith('.tgz'));
    if (packed.length !== 1)
      throw new Error(`bun pm pack wrote ${JSON.stringify(packed)}, expected one .tgz`);
    const tarball = await ctx.output.file(join(out, packed[0]!), { name: 'package.tgz' });
    ctx.log('SDK checked and packed', { name, version });
    return { name, version, tarball };
  },
);
