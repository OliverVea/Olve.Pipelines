import { implement, step, external, poll } from "@olivervea/pipelines";
import { SIGNAL_TEST } from "../shared";
import * as M from "../models";

export const polled = implement(step({
  name: "polled",
  description: "Waits until the signal-test service's status has a value.",
  image: "alpine:3.22",
  input: {
    signal: external(M.Signal, {
      title: "Did the polled value arrive?",
      from: [poll({ url: `${SIGNAL_TEST}/status/{run.id}`, valuePath: "signal", intervalSeconds: 10 })],
    }),
  },
  timeoutSeconds: 120,
}), async ({ signal }, ctx) => {
  if (!signal.ok || signal.via !== "poll") throw new Error(`unexpected signal ${JSON.stringify(signal)}`);
  ctx.log("polled", signal);
});
