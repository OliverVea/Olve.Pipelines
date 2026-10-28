import { implement } from '@olivervea/pipelines';
import { publishCli } from '../pipeline';

export default implement(publishCli, async ({ build }, ctx) => {
  if (!ctx.config.publish.cli.enabled)
    return ctx.skip('publishing the CLI is turned off (publish.cli.enabled)');
  const linuxX64 = await ctx.output.file(await ctx.files.fetch(build.linuxX64), {
    name: 'pl-linux-x64',
  });
  const winX64 = await ctx.output.file(await ctx.files.fetch(build.winX64), {
    name: 'pl-win-x64.exe',
  });
  if (linuxX64.sha256 !== build.linuxX64.sha256 || winX64.sha256 !== build.winX64.sha256)
    throw new Error('the released binaries differ from the build');
  ctx.log('released pl', { version: build.version });
  return { version: build.version, linuxX64, winX64 };
});
