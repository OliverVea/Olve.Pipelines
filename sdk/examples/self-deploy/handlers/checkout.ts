import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { checkout } from "../pipeline";

// Fake checkout: materialises a tiny tree instead of downloading the repo tarball.
export default implement(checkout, async ({ repo, sha }, ctx) => {
  const dir = join(ctx.tmpDir, `tree-${sha.slice(0, 7)}`);
  mkdirSync(join(dir, "src"), { recursive: true });
  writeFileSync(join(dir, "README.md"), `# ${repo}@${sha}\n`);
  writeFileSync(join(dir, "src", "main.ts"), "console.log('hi');\n");
  ctx.log("checked out", { repo, sha });
  return { repo, sha, tree: await ctx.output.dir(dir, { name: "tree" }) };
});
