import { implement, step, external, callback } from "@olivervea/pipelines";
import * as M from "../models";

export const calledBack = implement(step({
  name: "called-back",
  description: "Waits for the signal-test service's callback.",
  image: "alpine:3.22",
  input: { signal: external(M.Signal, { title: "Did the callback arrive?", from: [callback()] }) },
  timeoutSeconds: 120,
}), async ({ signal }, ctx) => {
  if (!signal.ok || signal.via !== "callback") throw new Error(`unexpected signal ${JSON.stringify(signal)}`);
  ctx.log("called back", signal);
});
