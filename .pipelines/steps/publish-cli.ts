import { implement, step } from '@olivervea/pipelines';
import { PUBLISH, REGISTRY_SECRETS } from '../config';
import * as M from '../models';

export const publishCli = implement(
  step({
    name: 'publish-cli',
    description:
      "Releases this run's pl build: the instance serves it at /download once the run is promoted.",
    image: 'alpine:3.22',
    input: { build: M.CliBuild },
    output: M.CliRelease,
    config: [PUBLISH],
    secrets: REGISTRY_SECRETS,
    timeoutSeconds: 300,
  }),
  async ({ build }, ctx) => {
    if (!ctx.config.publish.cli.enabled)
      return ctx.skip('publishing the CLI is turned off (publish.cli.enabled)');
    const linuxX64 = await ctx.output.file(await ctx.files.fetch(build.linuxX64), {
      name: 'pl-linux-x64',
    });
    const winX64 = await ctx.output.file(await ctx.files.fetch(build.winX64), {
      name: 'pl-win-x64.exe',
    });
    if (
      linuxX64.sha256 !== build.linuxX64.sha256 ||
      winX64.sha256 !== build.winX64.sha256
    )
      throw new Error('the released binaries differ from the build');
    ctx.log('released pl', { version: build.version });
    return { version: build.version, linuxX64, winX64 };
  },
);
