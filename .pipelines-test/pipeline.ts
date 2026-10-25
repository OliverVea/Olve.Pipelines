// Fixture pipeline for Olve.Pipelines v2 — NOT a real deploy. Bound on the v2 instance (path
// .pipelines-test) to prove reconcile → version → triggers → stages → harness step Jobs → outputs.
//
//   hello ─┬─ count ─┐
//          └─ echo   ├─ when(mode = normal): last
//                    ┘     └─ when(target ∈ beta, prod): deploy-beta
//                            └─ when(target = prod): deploy-prod
//                               └─ when(smoke, mode = normal): smoke
//   select(notify.channel): none | log: notify-log | webhook: notify-webhook
//   onFailure: log-failure (mode = fail makes echo throw)
// Four images (alpine, debian, busybox) exercise the bundled loader; hello → count passes a file ref.
import { config, FailureContext, pipeline, step, z } from "@olivervea/pipelines";
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
  .onPush(e => ({ sha: e.after }))
  .onPoll({ name: "poll", url: "https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/v2", valuePath: "sha", intervalSeconds: 3600 },
          value => ({ sha: value }))
  .stage(hello)
  .stage(count, echo)
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
