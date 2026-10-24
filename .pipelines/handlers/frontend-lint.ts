import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { frontendLint } from "../pipeline";

export default implement(frontendLint, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const env = { npm_config_cache: join(ctx.tmpDir, "npm-cache"), npm_config_update_notifier: "false" };
  await ctx.exec(["npm", "install", "--no-audit", "--no-fund"], { cwd: join(tree, "clients/olve-pipelines-client-ts"), env });
  const frontend = join(tree, "frontend");
  await ctx.exec(["npm", "ci", "--no-audit", "--no-fund"], { cwd: frontend, env });
  await ctx.exec(["npm", "run", "lint"], { cwd: frontend, env });
  await ctx.exec(["npm", "run", "format:check"], { cwd: frontend, env });
  ctx.log("frontend lint and formatting OK");
});
