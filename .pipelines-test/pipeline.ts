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
import { callback, config, external, FailureContext, pipeline, poll, step, z } from "@olivervea/pipelines";
import * as M from "./models";

export const hello = step({
  name: "hello",
  description: "Writes a greeting file and passes it on as a file ref.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  output: M.Greeting,
  timeoutSeconds: 300,
});

export const count = step({
  name: "count",
  description: "Counts the greeting's lines.",
  image: "debian:bookworm-slim",
  input: { greeting: M.Greeting },
  output: M.LineCount,
  timeoutSeconds: 300,
});

export const echo = step({
  name: "echo",
  description: "Echoes the request; fails on purpose when mode is fail.",
  image: "busybox:1.37",
  input: { request: M.TestInput },
  timeoutSeconds: 300,
});

// beta's signal-test companion (helm/values-v2-beta.yaml): answers the two external inputs below.
export const SIGNAL_TEST = "http://olve-signal-test.apps-beta.svc.cluster.local:8080";

export const sendSignals = step({
  name: "send-signals",
  description: "Asks the signal-test service to call back called-back and to publish the value polled polls.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
});

export const calledBack = step({
  name: "called-back",
  description: "Waits for the signal-test service's callback.",
  image: "alpine:3.22",
  input: { signal: external(M.Signal, { title: "Did the callback arrive?", from: [callback()] }) },
  timeoutSeconds: 120,
});

export const polled = step({
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
});

export const last = step({
  name: "last",
  description: "Summarises the greeting and its line count.",
  image: "alpine:3.22",
  input: { greeting: M.Greeting, count: M.LineCount },
  output: M.Summary,
  timeoutSeconds: 300,
});

export const NOTIFY = config("notify", "Where the fixture's fake notification goes.", z.discriminatedUnion("channel", [
  z.object({ channel: z.literal("none") }),
  z.object({ channel: z.literal("log"), prefix: z.string().default("notify") }),
  z.object({ channel: z.literal("webhook"), url: z.url() }),
]).prefault({ channel: "log" }));

export const deployBeta = step({
  name: "deploy-beta",
  description: "Pretends to deploy to beta.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
});

export const deployProd = step({
  name: "deploy-prod",
  description: "Pretends to deploy to prod.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
});

export const smoke = step({
  name: "smoke",
  description: "Pretends to smoke-test prod.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  timeoutSeconds: 120,
});

export const notifyLog = step({
  name: "notify-log",
  description: "Logs a fake notification.",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  config: [NOTIFY],
  timeoutSeconds: 120,
});

export const notifyWebhook = step({
  name: "notify-webhook",
  description: "Logs the webhook a notification would go to (sends nothing).",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  config: [NOTIFY],
  timeoutSeconds: 120,
});

export const logFailure = step({
  name: "log-failure",
  description: "Logs the failure context it is given.",
  image: "alpine:3.22",
  input: { failure: FailureContext },
  timeoutSeconds: 120,
});

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
