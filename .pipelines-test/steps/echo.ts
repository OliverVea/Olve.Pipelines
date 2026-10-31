import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";

export const echo = implement(step({
  name: "echo",
  description: "Echoes the request; fails on purpose when mode is fail.",
  image: "busybox:1.37",
  input: { request: M.TestInput },
  timeoutSeconds: 300,
}), async ({ request }, ctx) => {
  if (request.mode === "fail") throw new Error(`echo: failing on purpose (mode=fail, sha=${request.sha})`);
  await ctx.sh(`echo "echo: sha=$SHA mode=$MODE"`, { env: { SHA: request.sha, MODE: request.mode } });
});
