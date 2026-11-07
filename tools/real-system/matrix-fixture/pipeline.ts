// The fixture with a matrix step, committed over .pipelines/pipeline.ts by the matrix flow tests. `paint`
// runs once per cell and writes a file; `gather` reads every cell's output and file. With `failCell` set,
// that cell fails and the others, which hold until cancelled, are cancelled by the cascade.
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { cascadeFailure, cells, fileRef, implement, model, pipeline, step, z } from "@olivervea/pipelines";

const Input = model("Input", z.object({ failCell: z.string().default("") }));
const Painted = model("Painted", z.object({ label: z.string(), file: fileRef() }));
const Gathered = model("Gathered", z.object({ labels: z.array(z.string()), texts: z.array(z.string()) }));

const paint = implement(step({
  name: "paint",
  description: "Writes a file named after its cell.",
  image: "alpine:3.22",
  input: { request: Input },
  output: Painted,
  timeoutSeconds: 300,
  matrix: {
    axes: { shape: ["square", "circle"], size: ["s", "l"] },
    exclude: [{ shape: "circle", size: "l" }],
    failure: cascadeFailure(),
  },
}), async ({ request }, ctx) => {
  const label = `${ctx.cell.shape}-${ctx.cell.size}`;
  if (request.failCell === label) throw new Error(`cell ${label} was asked to fail`);
  if (request.failCell) await new Promise((resolve) => ctx.signal.addEventListener("abort", resolve));
  const path = join(ctx.tmpDir, "cell.txt");
  writeFileSync(path, `painted ${label}`);
  return { label, file: await ctx.output.file(path, { name: "cell.txt" }) };
});

const gather = implement(step({
  name: "gather",
  description: "Reads every cell's output and file.",
  image: "alpine:3.22",
  input: { painted: cells(Painted) },
  output: Gathered,
  timeoutSeconds: 120,
}), async ({ painted }, ctx) => ({
  labels: painted.map((p) => `${Object.values(p.cell).join("-")}=${p.output.label}`),
  texts: await Promise.all(painted.map(async (p) => readFileSync(await ctx.files.fetch(p.output.file), "utf8"))),
}));

export default pipeline({
  name: "__PIPELINE_NAME__",
  description: "Real-system test fixture with a matrix step.",
  input: Input,
})
  .stage(paint)
  .stage(gather);
