import { implement } from "../../sdk";
import { checkout } from "../pipeline";

console.error("[side-effect] handlers/checkout.ts evaluated");

export default implement(checkout, async ({ repo, sha }, ctx) => {
  const tree = await ctx.output.file("tree.txt", `README.md\nsrc/main.ts\nsrc/lib.ts\n# ${repo}@${sha}\n`);
  return { repo, sha, tree };
});
