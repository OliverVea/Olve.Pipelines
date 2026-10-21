import { implement } from "@olve/pipelines";
import { aoeTriage } from "../pipeline";

export default implement(aoeTriage, ({ failure }, ctx) => {
  ctx.log("would open an agent session", {
    run: failure.run.address,
    steps: failure.failedSteps.map(f => f.step),
    reason: failure.reason,
  });
});
