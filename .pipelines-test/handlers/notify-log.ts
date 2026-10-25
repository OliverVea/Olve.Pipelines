import { implement } from "@olivervea/pipelines";
import { notifyLog } from "../pipeline";

export default implement(notifyLog, ({ request }, ctx) => {
  const notify = ctx.config.notify;
  ctx.log(`${notify.channel === "log" ? notify.prefix : "notify"}: run for ${request.sha} finished`);
});
