import { implement } from '@olivervea/pipelines';
import { publishCli } from '../pipeline';

export default implement(publishCli, async ({ linux, windows }, ctx) => {
  if (!ctx.config.publish.cli.enabled)
    return ctx.skip('publishing the CLI is turned off (publish.cli.enabled)');
  if (linux.version !== windows.version)
    throw new Error(
      `the Linux (${linux.version}) and Windows (${windows.version}) builds differ in version`,
    );
  const linuxX64 = await ctx.output.file(await ctx.files.fetch(linux.binary), {
    name: 'pl-linux-x64',
  });
  const winX64 = await ctx.output.file(await ctx.files.fetch(windows.binary), {
    name: 'pl-win-x64.exe',
  });
  if (linuxX64.sha256 !== linux.binary.sha256 || winX64.sha256 !== windows.binary.sha256)
    throw new Error('the released binaries differ from the build');
  ctx.log('released pl', { version: linux.version });
  return { version: linux.version, linuxX64, winX64 };
});
