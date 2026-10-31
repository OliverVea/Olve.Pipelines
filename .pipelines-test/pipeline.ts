// Fixture pipeline for Olve.Pipelines v2, NOT a real deploy. Bound on beta (path .pipelines-test, polling off):
// the deployer's beta-test reconciles it and runs it at each commit (manual trigger).
//
//   hello ─┬─ count ─┐
//          └─ echo   ├─ send-signals ─┬─ called-back (callback input)
//                    │                └─ polled (poll input)
//                    ├─ when(mode = normal): last
//                    ┘     └─ when(target ∈ beta, prod): deploy-beta
//                            └─ when(target = prod): deploy-prod
//                               └─ when(smoke, mode = normal): smoke
//   select(notify.channel): none | log: notify-log | webhook: notify-webhook
//   onFailure: log-failure (mode = fail makes echo throw)
// Four images (alpine, debian, busybox) exercise the bundled loader; hello → count passes a file ref.
import { pipeline } from "@olivervea/pipelines";
import * as M from "./models";
import { NOTIFY } from "./shared";
import { calledBack } from "./steps/called-back";
import { count } from "./steps/count";
import { deployBeta } from "./steps/deploy-beta";
import { deployProd } from "./steps/deploy-prod";
import { echo } from "./steps/echo";
import { hello } from "./steps/hello";
import { last } from "./steps/last";
import { logFailure } from "./steps/log-failure";
import { notifyLog } from "./steps/notify-log";
import { notifyWebhook } from "./steps/notify-webhook";
import { polled } from "./steps/polled";
import { sendSignals } from "./steps/send-signals";
import { smoke } from "./steps/smoke";

export default pipeline({
  name: "v2 test fixture",
  description: "End-to-end fixture: file refs between steps, a parallel stage, nested and multi-field when blocks, and a config select.",
  input: M.TestInput,
})
  .stage(hello)
  .stage(count, echo)
  .stage(sendSignals)
  .stage(calledBack, polled)
  .when({ mode: "normal" }, p => p
    .stage(last)
    .when({ target: ["beta", "prod"] }, q => q
      .stage(deployBeta)
      .when({ target: "prod" }, r => r
        .stage(deployProd)
        .when({ smoke: true, mode: "normal" }, t => t.stage(smoke)))))
  .select(NOTIFY, {
    none: p => p,
    log: p => p.stage([notifyLog, from => ({ request: from.input })]),
    webhook: p => p.stage([notifyWebhook, from => ({ request: from.input })]),
  })
  .onFailure(logFailure);
