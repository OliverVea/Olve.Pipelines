import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";

export const deployBeta = implement(step({
  name: "deploy-beta",
  description: "Pretends to deploy to beta.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
}), async ({ request }, ctx) => {
  await ctx.sh(`echo "deploy-beta: pretending for $SHA (target=$TARGET)"`, { env: { SHA: request.sha, TARGET: request.target } });
});
