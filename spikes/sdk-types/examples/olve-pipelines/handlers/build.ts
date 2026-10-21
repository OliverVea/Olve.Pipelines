import { implement } from "@olve/pipelines";
import { build } from "../pipeline";

export default implement(build, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const version = ctx.run.version;
  await ctx.sh("scripts/build.sh", {
    cwd: tree,
    env: { VERSION: version, GITHUB_TOKEN: ctx.secrets.GITHUB_TOKEN, OUT: "/olve/out" },
  });
  return {
    version,
    commit: { repo: source.repo, sha: source.sha },
    image: await ctx.output.file("image.tar"),
    chart: await ctx.output.dir("chart"),
  };
});
