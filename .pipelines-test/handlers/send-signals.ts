import { implement } from "@olivervea/pipelines";
import { calledBack, sendSignals, SIGNAL_TEST } from "../pipeline";

export default implement(sendSignals, async (_, ctx) => {
  const send = async (path: string, method: string, body: unknown) => {
    const res = await ctx.http.fetch(`${SIGNAL_TEST}${path}`, { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    if (!res.ok) throw new Error(`${method} ${path} answered ${res.status}: ${await res.text()}`);
  };
  await send("/callback", "POST", { url: ctx.inputUrl(calledBack, "signal"), value: { ok: true, via: "callback" }, delaySeconds: 5 });
  await send(`/status/${ctx.run.id}`, "PUT", { value: { signal: { ok: true, via: "poll" } }, delaySeconds: 15 });
});
