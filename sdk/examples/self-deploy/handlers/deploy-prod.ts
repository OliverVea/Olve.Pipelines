import { implement } from "@olivervea/pipelines";
import { deployProd } from "../pipeline";
import deployScript from "../scripts/deploy.sh" with { type: "file" };
import { releaseName } from "./lib/release";

export default implement(deployProd, async ({ artifact }, ctx) => {
  const release = releaseName("prod");
  await ctx.sh(deployScript, {
    env: {
      RELEASE: release,
      VERSION: artifact.version,
      IMAGE_TAR: await ctx.files.fetch(artifact.image),
      CHART_DIR: await ctx.files.fetch(artifact.chart),
    },
  });
  return { release, version: artifact.version, url: "https://pipelines-v2.ovea.pro" };
});
