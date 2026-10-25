import { implement } from "@olivervea/pipelines";
import { deployBeta } from "../pipeline";

export default implement(deployBeta, async ({ request }, ctx) => {
  await ctx.sh(`echo "deploy-beta: pretending for $SHA (target=$TARGET)"`, { env: { SHA: request.sha, TARGET: request.target } });
});
