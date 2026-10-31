import { implement, step } from '@olivervea/pipelines';
import * as M from '../models';
import { releaseName } from '../release';
import { SSH_PRIVATE_KEY } from '../secrets';
import deployScript from '../scripts/deploy.sh' with { type: 'file' };

export const deployProd = implement(
  step({
    name: 'deploy-prod',
    description: 'Deploys to prod.',
    image: 'alpine:latest',
    input: { artifact: M.BuildArtifact },
    output: M.ProdDeployment,
    secrets: [SSH_PRIVATE_KEY],
  }),
  async ({ artifact }, ctx) => {
    const release = releaseName('prod');
    await ctx.sh(deployScript, {
      env: {
        RELEASE: release,
        VERSION: artifact.version,
        IMAGE_TAR: await ctx.files.fetch(artifact.image),
        CHART_DIR: await ctx.files.fetch(artifact.chart),
      },
    });
    return { release, version: artifact.version, url: 'https://pipelines-v2.ovea.pro' };
  },
);
