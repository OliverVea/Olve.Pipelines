import { implement } from "../../sdk";
import { deployProd } from "../pipeline";
import { releaseName } from "./lib/version";

export default implement(deployProd, async ({ artifact, decision }, ctx) => {
  if (!decision.approved) return ctx.skip("not approved");
  await ctx.sh(`echo "deploying ${artifact.version} to prod"`);
  return { release: releaseName("prod"), version: artifact.version, url: "https://pipelines.ovea.pro" };
});
