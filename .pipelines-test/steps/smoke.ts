import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";

export const smoke = implement(step({
  name: "smoke",
  description: "Pretends to smoke-test prod.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
}), async ({ request }, ctx) => {
  await ctx.sh(`echo "smoke: pretending for $SHA (target=$TARGET)"`, { env: { SHA: request.sha, TARGET: request.target } });
});
