import { implement, step } from "@olivervea/pipelines";
import { NOTIFY } from "../shared";
import * as M from "../models";

export const notifyLog = implement(step({
  name: "notify-log",
  description: "Logs a fake notification.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  config: [NOTIFY],
  timeoutSeconds: 120,
}), ({ request }, ctx) => {
  const notify = ctx.config.notify;
  ctx.log(`${notify.channel === "log" ? notify.prefix : "notify"}: run for ${request.sha} finished`);
});
