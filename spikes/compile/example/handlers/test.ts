import { implement } from "../../sdk";
import { test } from "../pipeline";
import testScript from "../scripts/test.sh" with { type: "file" };

export default implement(test, async ({ source }, ctx) => {
  const out = await ctx.sh.text({ script: testScript }, {
    env: { REPO: source.repo, SHA: source.sha, TREE: `${process.env.OLVE_DIR ?? "/olve"}/files/${source.tree.path}` },
  });
  return JSON.parse(out);
});
