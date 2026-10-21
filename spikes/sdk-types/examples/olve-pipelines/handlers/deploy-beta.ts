import { implement } from "@olve/pipelines";
import { deployBeta } from "../pipeline";

const RELEASE = "olve-pipelines";
const URL = "https://pipelines-beta.ovea.pro";

export default implement(deployBeta, async ({ artifact }, ctx) => {
  await ctx.sh("scripts/deploy-beta.sh", {
    env: {
      RELEASE,
      VERSION: artifact.version,
      IMAGE_TAR: await ctx.files.fetch(artifact.image),
      CHART_DIR: await ctx.files.fetch(artifact.chart),
      SSH_PRIVATE_KEY: ctx.secrets.SSH_PRIVATE_KEY,
    },
  });
  await ctx.retry({ attempts: 5, delaySeconds: 5 }, () => ctx.http.expectOk(`${URL}/api/health`));
  return { release: RELEASE, version: artifact.version, url: URL };
});
