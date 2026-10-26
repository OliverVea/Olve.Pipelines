import { implement } from "@olivervea/pipelines";
import { polled } from "../pipeline";

export default implement(polled, async ({ signal }, ctx) => {
  if (!signal.ok || signal.via !== "poll") throw new Error(`unexpected signal ${JSON.stringify(signal)}`);
  ctx.log("polled", signal);
});
