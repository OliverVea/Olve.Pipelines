import { implement, step } from '@olivervea/pipelines';
import * as M from '../models';
import { releaseName } from '../release';
import { SSH_PRIVATE_KEY } from '../secrets';
import deployScript from '../scripts/deploy.sh' with { type: 'file' };

const URL = 'https://pipelines-beta.ovea.pro';

export const deployBeta = implement(
  step({
    name: 'deploy-beta',
    description: 'Deploys to beta.',
    image: 'alpine:latest',
    input: { artifact: M.BuildArtifact },
    output: M.BetaDeployment,
    secrets: [SSH_PRIVATE_KEY],
    timeoutSeconds: 900,
  }),
  async ({ artifact }, ctx) => {
    if (process.env.EXAMPLE_SKIP_BETA) return ctx.skip('EXAMPLE_SKIP_BETA is set');
    const release = releaseName('beta');
    await ctx.sh(deployScript, {
      env: {
        RELEASE: release,
        VERSION: artifact.version,
        IMAGE_TAR: await ctx.files.fetch(artifact.image),
        CHART_DIR: await ctx.files.fetch(artifact.chart),
        SSH_KEY_LENGTH: String(ctx.secrets.SSH_PRIVATE_KEY.length),
      },
    });
    return {
      release,
      version: artifact.version,
      url: process.env.EXAMPLE_BETA_URL ?? URL,
    };
  },
);
