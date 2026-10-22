import { implement } from "@olivervea/pipelines";
import { build } from "../fixtures";

// Files are references produced by ctx.output.file/dir — not paths.
// @ts-expect-error -- expect: Types of property 'image' are incompatible
implement(build, async ({ source }, ctx) => ({
  version: "1",
  commit: { repo: source.repo, sha: source.sha },
  // (error lands on the implement call: async returns are not elaborated)
  image: "/olve/out/image.tar",
  chart: await ctx.output.dir("chart"),
}));
