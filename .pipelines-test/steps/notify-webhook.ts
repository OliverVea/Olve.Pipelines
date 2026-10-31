import { implement, step } from "@olivervea/pipelines";
import { NOTIFY } from "../shared";
import * as M from "../models";

export const notifyWebhook = implement(step({
  name: "notify-webhook",
  description: "Logs the webhook a notification would go to (sends nothing).",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  config: [NOTIFY],
  timeoutSeconds: 120,
}), ({ request }, ctx) => {
  const notify = ctx.config.notify;
  ctx.log("would notify", { url: notify.channel === "webhook" ? notify.url : null, sha: request.sha });
});
