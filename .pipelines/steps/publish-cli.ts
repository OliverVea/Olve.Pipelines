import { cells, implement, step } from '@olivervea/pipelines';
import { CLI_FILES } from './build-cli';
import { PUBLISH, REGISTRY_SECRETS } from '../config';
import * as M from '../models';

export const publishCli = implement(
  step({
    name: 'publish-cli',
    description:
      "Releases this run's pl build: the instance serves it at /download once the run is promoted.",
    image: 'alpine:3.22',
    input: { builds: cells(M.CliBinary) },
    output: M.CliRelease,
    config: [PUBLISH],
    secrets: REGISTRY_SECRETS,
    timeoutSeconds: 300,
  }),
  async ({ builds }, ctx) => {
    if (!ctx.config.publish.cli.enabled)
      return ctx.skip('publishing the CLI is turned off (publish.cli.enabled)');
    const release = async (platform: keyof typeof CLI_FILES) => {
      const build = builds.find((b) => `${b.cell.os}-${b.cell.arch}` === platform);
      if (!build) throw new Error(`build-cli has no ${platform} cell`);
      const file = await ctx.output.file(await ctx.files.fetch(build.output.binary), {
        name: CLI_FILES[platform],
      });
      if (file.sha256 !== build.output.binary.sha256)
        throw new Error(`the released ${CLI_FILES[platform]} differs from the build`);
      return { version: build.output.version, file };
    };
    const linux = await release('linux-x64');
    const windows = await release('windows-x64');
    ctx.log('released pl', { version: linux.version });
    return { version: linux.version, linuxX64: linux.file, winX64: windows.file };
  },
);
