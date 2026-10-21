import { implement } from "@olve/pipelines";
import { aoeTriage } from "../pipeline";

export default implement(aoeTriage, async ({ failure }, ctx) => {
  await ctx.sh("scripts/aoe-triage.sh", {
    env: { STEP: failure.step, RUN: String(failure.run), LOG_URL: failure.logUrl },
  });
});
