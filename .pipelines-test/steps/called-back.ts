import { implement, step, external, callback } from "@olivervea/pipelines";
import { SIGNAL_TEST } from "../shared";
import * as M from "../models";

export const calledBack = implement(step({
  name: "called-back",
  description: "Asks the signal-test service for a callback in its preflight, then waits for it.",
  image: "alpine:3.22",
  input: { signal: external(M.Signal, { title: "Did the callback arrive?", from: [callback()] }) },
  timeoutSeconds: 120,
}), async ({ signal }, ctx) => {
  if (!signal.ok || signal.via !== "callback") throw new Error(`unexpected signal ${JSON.stringify(signal)}`);
  ctx.log("called back", signal);
}, {
  // At once: the callback may well arrive while the preflight is still running.
  preflight: async (_, ctx) => {
    const res = await ctx.http.fetch(`${SIGNAL_TEST}/callback`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ url: ctx.inputUrl("called-back", "signal"), value: { ok: true, via: "callback" }, delaySeconds: 0 }),
    });
    if (!res.ok) throw new Error(`POST /callback answered ${res.status}: ${await res.text()}`);
    ctx.link(SIGNAL_TEST, "Signal test");
  },
});
