import { join } from 'node:path';
import { implement } from '@olivervea/pipelines';
import { buildCliWindows } from '../pipeline';
import { CLI, cliPublishArgs } from '../cli';

export default implement(buildCliWindows, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const out = join(ctx.tmpDir, 'cli');
  await ctx.exec(
    [
      'dotnet',
      'publish',
      CLI,
      '-r',
      'win-x64',
      ...cliPublishArgs(ctx.run.version),
      '-p:PublishAot=false',
      '-p:PublishSingleFile=true',
      '-p:SelfContained=true',
      '-o',
      out,
    ],
    { cwd: tree, env: { MSBUILDDISABLENODEREUSE: '1' } },
  );

  const binary = await ctx.output.file(join(out, 'pl.exe'), { name: 'pl-win-x64.exe' });
  ctx.log('built pl for Windows', { version: ctx.run.version });
  return { version: ctx.run.version, binary };
});
