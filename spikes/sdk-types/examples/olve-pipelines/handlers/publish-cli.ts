import { implement } from "@olve/pipelines";
import { publishCli } from "../pipeline";

export default implement(publishCli, async ({ artifact, prod }, ctx) => {
  const tag = `pl-v${artifact.version}`;
  const out = await ctx.sh.text("scripts/publish-cli.sh", {
    env: { TAG: tag, GITHUB_TOKEN: ctx.secrets.GITHUB_TOKEN, SERVER_URL: prod.url },
  });
  return { tag, assets: out.trim().split("\n") };
});
