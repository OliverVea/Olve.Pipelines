import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";

export const deployProd = implement(step({
  name: "deploy-prod",
  description: "Pretends to deploy to prod.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
}), async ({ request }, ctx) => {
  await ctx.sh(`echo "deploy-prod: pretending for $SHA (target=$TARGET)"`, { env: { SHA: request.sha, TARGET: request.target } });
});
