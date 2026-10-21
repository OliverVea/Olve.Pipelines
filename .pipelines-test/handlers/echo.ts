import { implement } from "@olve/pipelines";
import { echo } from "../pipeline";

export default implement(echo, async ({ request }, ctx) => {
  if (request.mode === "fail") throw new Error(`echo: failing on purpose (mode=fail, sha=${request.sha})`);
  await ctx.sh(`echo "echo: sha=$SHA mode=$MODE"`, { env: { SHA: request.sha, MODE: request.mode } });
});
