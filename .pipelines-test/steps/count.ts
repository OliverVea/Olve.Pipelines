import { readFileSync } from "node:fs";
import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";

export const count = implement(step({
  name: "count",
  description: "Counts the greeting's lines.",
  image: "debian:bookworm-slim",
  input: { greeting: M.Greeting },
  output: M.LineCount,
  timeoutSeconds: 300,
}), async ({ greeting }, ctx) => {
  const path = await ctx.files.fetch(greeting.file);
  const lines = readFileSync(path, "utf8").split("\n").filter(l => l.length > 0).length;
  ctx.log("counted", { lines, message: greeting.message });
  return { lines };
});
