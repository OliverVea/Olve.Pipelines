import { readFileSync } from "node:fs";
import { implement } from "@olve/pipelines";
import { count } from "../pipeline";

export default implement(count, async ({ greeting }, ctx) => {
  const path = await ctx.files.fetch(greeting.file);
  const lines = readFileSync(path, "utf8").split("\n").filter(l => l.length > 0).length;
  ctx.log("counted", { lines, message: greeting.message });
  return { lines };
});
