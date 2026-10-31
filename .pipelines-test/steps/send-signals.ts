import { implement, step } from "@olivervea/pipelines";
import { SIGNAL_TEST } from "../shared";
import { calledBack } from "./called-back";
import * as M from "../models";

export const sendSignals = implement(step({
  name: "send-signals",
  description: "Asks the signal-test service to call back called-back and to publish the value polled polls.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
}), async (_, ctx) => {
  const send = async (path: string, method: string, body: unknown) => {
    const res = await ctx.http.fetch(`${SIGNAL_TEST}${path}`, { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    if (!res.ok) throw new Error(`${method} ${path} answered ${res.status}: ${await res.text()}`);
  };
  await send("/callback", "POST", { url: ctx.inputUrl(calledBack, "signal"), value: { ok: true, via: "callback" }, delaySeconds: 5 });
  await send(`/status/${ctx.run.id}`, "PUT", { value: { signal: { ok: true, via: "poll" } }, delaySeconds: 15 });
});
