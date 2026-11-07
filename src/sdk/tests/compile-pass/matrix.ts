import { cascadeFailure, cells, implement, model, type Cell } from "@olivervea/pipelines";
import { testHandler } from "@olivervea/pipelines/testing";
import { base, checkout, step, z, Source } from "../fixtures2";
import type { Equal, Expect } from "../type-assert";

const BuiltBinary = model("BuiltBinary", z.object({ name: z.string() }));

export const buildCli = implement(
  step({
    name: "build-cli",
    description: "",
    image: "alpine",
    input: { source: Source },
    output: BuiltBinary,
    matrix: {
      axes: { os: ["linux", "windows", "macos"], arch: ["x64", "arm64"] },
      exclude: [{ os: "windows", arch: "arm64" }, { arch: "arm64" }],
      failure: cascadeFailure(),
      maxParallel: 2,
    },
  }),
  async ({ source }, ctx) => {
    type _ = Expect<Equal<typeof ctx.cell, { readonly os: "linux" | "windows" | "macos"; readonly arch: "x64" | "arm64" }>>;
    const { os, arch } = ctx.cell;
    return { name: `${source.sha}-${os}-${arch}` };
  },
);

const publish = implement(
  step({ name: "publish", description: "", image: "alpine", input: { binaries: cells(BuiltBinary) } }),
  ({ binaries }) => {
    type _ = Expect<Equal<(typeof binaries)[number]["cell"], Cell>>;
    for (const b of binaries) {
      const os: string | undefined = b.cell.os;
      const name: string = b.output.name;
      void os, name;
    }
  },
);

const p = base()
  .stage([checkout, (from) => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(buildCli);
// matched by model
p.stage(publish);
// or wired whole with a mapper
p.stage([publish, (from) => ({ binaries: from["build-cli"] })]);

// a matrix step without an output, and a one-axis matrix
implement(
  step({ name: "lint", description: "", image: "alpine", matrix: { axes: { ts: ["7.0", "5.9"] } } }),
  (_input, ctx) => {
    type _ = Expect<Equal<typeof ctx.cell, { readonly ts: "7.0" | "5.9" }>>;
  },
);

// testHandler takes the cell to run
testHandler(buildCli, { source: {} as never }, { cell: { os: "linux", arch: "x64" } });
