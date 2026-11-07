import { cells, implement, model } from "@olivervea/pipelines";
import { testHandler } from "@olivervea/pipelines/testing";
import { base, checkout, lint, step, z, Source } from "../fixtures2";

const BuiltBinary = model("BuiltBinary", z.object({ name: z.string() }));
const axes = { os: ["linux", "windows"], arch: ["x64", "arm64"] } as const;

// an exclude value that is not one of the axis's values
step({
  name: "a",
  description: "",
  image: "alpine",
  // @ts-expect-error -- expect: '"solaris"' is not assignable
  matrix: { axes, exclude: [{ os: "solaris" }] },
});

// an exclude naming an axis the step does not have
step({
  name: "b",
  description: "",
  image: "alpine",
  // @ts-expect-error -- expect: 'browser' does not exist
  matrix: { axes, exclude: [{ browser: "firefox" }] },
});

// ctx.cell exists only on a matrix step
implement(step({ name: "c", description: "", image: "alpine" }), (_i, ctx) => {
  // @ts-expect-error -- expect: Property 'cell' does not exist
  ctx.log(ctx.cell.os);
});

// a cell's values are the axis's literals
implement(step({ name: "d", description: "", image: "alpine", matrix: { axes } }), (_i, ctx) => {
  // @ts-expect-error -- expect: '"macos"' have no overlap
  if (ctx.cell.os === "macos") ctx.log("never");
});

const buildCli = implement(
  step({ name: "build-cli", description: "", image: "alpine", input: { source: Source }, output: BuiltBinary, matrix: { axes } }),
  () => ({ name: "x" }),
);
const one = step({ name: "one", description: "", image: "alpine", input: { binary: BuiltBinary } });
const all = step({ name: "all", description: "", image: "alpine", input: { binaries: cells(BuiltBinary) } });
const p = base()
  .stage([checkout, (from) => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(buildCli);

// a matrix step is read through cells(), never as one output
// @ts-expect-error -- expect: is produced by a matrix step; read it with cells(BuiltBinary)
p.stage(one);

// cells() reads a matrix step only
// @ts-expect-error -- expect: has no matrix step producing it at this stage
base().stage(all);

// a mapper takes the whole matrix step, not a field of it
// @ts-expect-error -- expect: Property 'name' does not exist
p.stage([one, (from) => ({ binary: { name: from["build-cli"].name } })]);

// nor a plain step into a cells() input
// @ts-expect-error -- expect: is not assignable
p.stage([all, (from) => ({ binaries: from.checkout })]);

// testHandler's cell is the step's cell type, and only for a matrix step
// @ts-expect-error -- expect: '"solaris"' is not assignable
testHandler(buildCli, { source: {} as never }, { cell: { os: "solaris", arch: "x64" } });
// @ts-expect-error -- expect: is not assignable to type 'undefined'
testHandler(lint, { source: {} as never }, { cell: { os: "linux" } });

// cells() can't read a matrix step that may be skipped
// @ts-expect-error -- expect: cells() cannot read a maybe-skipped matrix step
base().stage([checkout, (from) => ({ repo: from.input.repo, sha: from.input.sha })]).when({ deployTo: "prod" }, (q) => q.stage(buildCli)).stage(all);
