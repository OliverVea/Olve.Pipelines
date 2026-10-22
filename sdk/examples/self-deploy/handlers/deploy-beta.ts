import { implement } from "@olivervea/pipelines";
import { deployBeta } from "../pipeline";
import deployScript from "../scripts/deploy.sh" with { type: "file" };
import { releaseName } from "./lib/release";

const URL = "https://pipelines-v2-beta.ovea.pro";

export default implement(deployBeta, async ({ artifact }, ctx) => {
  if (process.env.EXAMPLE_SKIP_BETA) return ctx.skip("EXAMPLE_SKIP_BETA is set");
  const release = releaseName("beta");
  await ctx.sh(deployScript, {
    env: {
      RELEASE: release,
      VERSION: artifact.version,
      IMAGE_TAR: await ctx.files.fetch(artifact.image),
      CHART_DIR: await ctx.files.fetch(artifact.chart),
      SSH_KEY_LENGTH: String(ctx.secrets.SSH_PRIVATE_KEY.length),
    },
  });
  return { release, version: artifact.version, url: process.env.EXAMPLE_BETA_URL ?? URL };
});
