import { implement } from "@olve/pipelines";
import { echo } from "../pipeline";

export default implement(echo, async ({ request }, ctx) => {
  await ctx.sh(`echo "echo: sha=$SHA mode=$MODE"`, { env: { SHA: request.sha, MODE: request.mode } });
});
