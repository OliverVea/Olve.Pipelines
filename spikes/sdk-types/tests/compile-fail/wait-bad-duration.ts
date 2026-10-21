import { steps, M } from "../fixtures2";

steps.wait({
  name: "approve",
  output: M.ProdApproval,
  resolveBy: {
    // @ts-expect-error -- expect: Type '"5 minutes"' is not assignable to type
    check: { every: "5 minutes", async run(_i, ctx) { return ctx.notYet(); } },
  },
});
