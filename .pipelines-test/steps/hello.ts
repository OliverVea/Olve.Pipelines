import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";

export const hello = implement(step({
  name: "hello",
  description: "Writes a greeting file and passes it on as a file ref.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  output: M.Greeting,
  timeoutSeconds: 300,
}), async ({ request }, ctx) => {
  const path = join(ctx.tmpDir, "greeting.txt");
  writeFileSync(path, `hello from run ${ctx.run.number} (${ctx.run.version})\ncommit ${request.sha}\nmode ${request.mode}\nattempt ${ctx.run.attempt}\n`);
  const file = await ctx.output.file(path, { name: "greeting.txt" });
  const os = (await ctx.sh.text("head -n1 /etc/os-release")).trim();
  ctx.log("wrote greeting", { os });
  return { message: `hello ${request.sha.slice(0, 7)} (${os})`, file };
});
