import { implement } from "@olivervea/pipelines";
import { deployProd } from "../pipeline";

export default implement(deployProd, async ({ request }, ctx) => {
  await ctx.sh(`echo "deploy-prod: pretending for $SHA (target=$TARGET)"`, { env: { SHA: request.sha, TARGET: request.target } });
});
