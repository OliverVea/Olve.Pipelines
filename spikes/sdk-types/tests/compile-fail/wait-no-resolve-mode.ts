import { steps, M } from "../fixtures2";

steps.wait({
  name: "approve",
  output: M.ProdApproval,
  // @ts-expect-error -- expect: is not assignable to type
  resolveBy: {},
});
