// The pipeline the GitHub stand-in serves. Two stages: a file ref passes between steps, the second step
// reads a declared secret (and logs it, which the log must show masked), and `outcome: "fail"` fails the run.
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileRef, implement, model, pipeline, secret, step, z } from "@olivervea/pipelines";

const FIXTURE_SECRET = secret("FIXTURE_SECRET", "Read by the consume step.");

const Input = model("Input", z.object({
  message: z.string().default("hello"),
  outcome: z.enum(["succeed", "fail"]).default("succeed"),
}));

const Produced = model("Produced", z.object({ file: fileRef() }));
const Consumed = model("Consumed", z.object({ text: z.string(), secretLength: z.number() }));

const produce = implement(step({
  name: "produce",
  description: "Writes the input message to a file.",
  image: "alpine:3.22",
  input: { request: Input },
  output: Produced,
  timeoutSeconds: 120,
}), async ({ request }, ctx) => {
  const path = join(ctx.tmpDir, "message.txt");
  writeFileSync(path, request.message);
  ctx.log("produced the message");
  return { file: await ctx.output.file(path, { name: "message.txt" }) };
});

const consume = implement(step({
  name: "consume",
  description: "Reads the file back and fails the run on request.",
  image: "alpine:3.22",
  input: { request: Input, produced: Produced },
  output: Consumed,
  secrets: [FIXTURE_SECRET],
  timeoutSeconds: 120,
}), async ({ request, produced }, ctx) => {
  if (request.outcome === "fail") throw new Error("the fixture was asked to fail");
  const text = readFileSync(await ctx.files.fetch(produced.file), "utf8");
  const secret = ctx.secrets.FIXTURE_SECRET ?? "";
  ctx.log(`read the secret: ${secret}`);
  return { text, secretLength: secret.length };
});

export default pipeline({
  name: "__PIPELINE_NAME__",
  description: "Real-system test fixture.",
  input: Input,
})
  .stage(produce)
  .stage(consume);
