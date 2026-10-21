import { implement } from "../../sdk";
import { build } from "../pipeline";
import { versionFor } from "./lib/version";

export default implement(build, async ({ source }, ctx) => {
  const version = versionFor(ctx.run.version, source.sha);
  const image = await ctx.output.file("image.tar", `fake image ${version}`);
  return { version, commit: { repo: source.repo, sha: source.sha }, image };
});
