import { base, deployBeta } from "../fixtures";

base()
  // @ts-expect-error -- expect: a failure handler's inputs must all be FailureContext
  .onFailure(deployBeta);
