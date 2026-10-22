// Fixture pipeline for Olve.Pipelines v2 — NOT a real deploy. Bound on the v2 instance (path
// .pipelines-test) to prove reconcile → version → triggers → stages → harness step Jobs → outputs.
//
//   hello ─┬─ count ─┐
//          └─ echo   ├─ when(mode = normal): last
//                    ┘
//   onFailure: log-failure (mode = fail makes echo throw)
// Four images (alpine, debian, busybox) exercise the bundled loader; hello → count passes a file ref.
import { FailureContext, pipeline, step } from "@olivervea/pipelines";
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

export const logFailure = step({
  name: "log-failure",
  description: "Logs the failure context it is given.",
  image: "alpine:3.22",
  input: { failure: FailureContext },
  timeoutSeconds: 120,
});

export default pipeline({
  name: "v2 test fixture",
  description: "Tiny end-to-end fixture: file refs between steps, a parallel stage and a when block.",
  input: M.TestInput,
})
  .onPush(e => ({ sha: e.after }))
  .onPoll({ name: "poll", url: "https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/v2", valuePath: "sha", intervalSeconds: 600 },
          value => ({ sha: value }))
  .stage(hello)
  .stage(count, echo)
  .when({ mode: "normal" }, p => p.stage(last))
  .onFailure(logFailure);
