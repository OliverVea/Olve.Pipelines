import { implement, step } from '@olivervea/pipelines';
import * as M from '../models';
import { GITHUB_TOKEN } from '../secrets';

export const publishCli = implement(
  step({
    name: 'publish-cli',
    description: 'Publishes the pl CLI.',
    image: 'mcr.microsoft.com/dotnet/sdk:10.0-noble',
    input: { artifact: M.BuildArtifact, prod: M.ProdDeployment },
    output: M.CliRelease,
    secrets: [GITHUB_TOKEN],
  }),
  async ({ artifact, prod }, ctx) => {
    ctx.log('publishing pl', { version: artifact.version, after: prod.url });
    return { tag: `v${artifact.version}`, assets: ['pl-linux-x64', 'pl-win-x64.exe'] };
  },
);
