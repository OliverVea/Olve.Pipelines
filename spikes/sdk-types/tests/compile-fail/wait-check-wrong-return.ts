import { steps, M } from "../fixtures2";

steps.wait({
  name: "approve",
  input: { beta: M.BetaDeployment },
  output: M.ProdApproval,
  resolveBy: {
    check: {
      every: "5m",
      // @ts-expect-error -- expect: Property 'by' is missing
      async run({ beta }, ctx) {
        return beta.url ? { approved: true } : ctx.notYet();
      },
    },
  },
});
