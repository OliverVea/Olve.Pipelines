import { implement } from "@olivervea/pipelines";
import { logFailure } from "../pipeline";

export default implement(logFailure, ({ failure }, ctx) => {
  ctx.log("run failed", {
    run: failure.run.address,
    version: failure.version.number,
    steps: failure.failedSteps.map(f => `${f.step}@${f.attempt}`),
    reason: failure.reason,
  });
  console.log(JSON.stringify(failure, null, 2));
});
