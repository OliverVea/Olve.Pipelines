import { implement } from "@olve/pipelines";
import { aoeTriage } from "../pipeline";

export default implement(aoeTriage, ({ failure }, ctx) => {
  ctx.log("would open an agent session", { step: failure.step, run: failure.run, reason: failure.reason });
});
