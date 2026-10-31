import { implement, step, FailureContext } from "@olivervea/pipelines";

export const logFailure = implement(step({
  name: "log-failure",
  description: "Logs the failure context it is given.",
  image: "alpine:3.22",
  input: { failure: FailureContext },
  timeoutSeconds: 120,
}), ({ failure }, ctx) => {
  ctx.log("run failed", {
    run: failure.run.address,
    version: failure.version.number,
    steps: failure.failedSteps.map(f => `${f.step}@${f.attempt}`),
    reason: failure.reason,
  });
  console.log(JSON.stringify(failure, null, 2));
});
