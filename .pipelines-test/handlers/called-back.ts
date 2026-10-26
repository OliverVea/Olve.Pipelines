import { implement } from "@olivervea/pipelines";
import { calledBack } from "../pipeline";

export default implement(calledBack, async ({ signal }, ctx) => {
  if (!signal.ok || signal.via !== "callback") throw new Error(`unexpected signal ${JSON.stringify(signal)}`);
  ctx.log("called back", signal);
});
