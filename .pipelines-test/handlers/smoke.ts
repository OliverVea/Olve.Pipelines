import { implement } from "@olivervea/pipelines";
import { smoke } from "../pipeline";

export default implement(smoke, async ({ request }, ctx) => {
  await ctx.sh(`echo "smoke: pretending for $SHA (target=$TARGET)"`, { env: { SHA: request.sha, TARGET: request.target } });
});
