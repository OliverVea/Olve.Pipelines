import { join } from 'node:path';
import { implement } from '@olivervea/pipelines';
import { buildCli } from '../pipeline';
import { CLI, cliPublishArgs } from '../cli';

export default implement(buildCli, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const out = join(ctx.tmpDir, 'cli');
  await ctx.exec(
    [
      'dotnet',
      'publish',
      CLI,
      '-r',
      'linux-x64',
      ...cliPublishArgs(ctx.run.version),
      '-p:DebugType=none',
      '-o',
      out,
    ],
    { cwd: tree, env: { MSBUILDDISABLENODEREUSE: '1' } },
  );
  await ctx.exec([join(out, 'pl'), '--version']);

  const binary = await ctx.output.file(join(out, 'pl'), { name: 'pl-linux-x64' });
  ctx.log('built pl for Linux', { version: ctx.run.version });
  return { version: ctx.run.version, binary };
});
