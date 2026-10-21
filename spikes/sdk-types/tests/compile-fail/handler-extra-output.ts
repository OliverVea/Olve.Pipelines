import { implement } from "@olve/pipelines";
import { deployBeta } from "../fixtures";

// Block-bodied async returns get no excess-property check from TS; implement() restores it, but the
// error lands on the handler, not the return statement.
// @ts-expect-error -- expect: 'urll' is not a field of the output model
implement(deployBeta, async ({ artifact }) => {
  return { release: "r", version: artifact.version, url: "u", urll: "typo" };
});
