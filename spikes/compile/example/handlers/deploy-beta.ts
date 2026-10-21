import { implement } from "../../sdk";
import { deployBeta } from "../pipeline";
import { releaseName } from "./lib/version";

export default implement(deployBeta, async ({ artifact }, ctx) => {
  if (process.env.SKIP_BETA) return ctx.skip("SKIP_BETA set");
  await ctx.sh(`echo "deploying ${artifact.version} to beta"`);
  return { release: releaseName("beta"), version: artifact.version, url: process.env.BAD_URL ?? "https://pipelines-beta.ovea.pro" };
});
