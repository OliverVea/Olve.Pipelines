import { implement } from "@olivervea/pipelines";
import { notifyWebhook } from "../pipeline";

export default implement(notifyWebhook, ({ request }, ctx) => {
  const notify = ctx.config.notify;
  ctx.log("would notify", { url: notify.channel === "webhook" ? notify.url : null, sha: request.sha });
});
