// Fixture pipeline for Olve.Pipelines v2 — NOT a real deploy. Bound on the v2 instance (path
// .pipelines-test) to prove reconcile → version → triggers → stages → harness step Jobs → outputs.
//
//   hello ─┬─ count ─┐
//          └─ echo   ├─ when(mode = normal): last
//                    ┘
//   onFailure: log-failure (mode = fail makes echo throw)
// Four images (alpine, debian, busybox) exercise the bundled loader; hello → count passes a file ref.
import { FailureContext, pipeline, step } from "@olve/pipelines";
import * as M from "./models";

export const hello = step({
  name: "hello",
  image: "alpine:3.22",
  input: { request: M.TestInput },
  output: M.Greeting,
  timeoutSeconds: 300,
});

export const count = step({
  name: "count",
  image: "debian:bookworm-slim",
  input: { greeting: M.Greeting },
  output: M.LineCount,
  timeoutSeconds: 300,
});

export const echo = step({
  name: "echo",
  image: "busybox:1.37",
  input: { request: M.TestInput },
  timeoutSeconds: 300,
});

export const last = step({
  name: "last",
  image: "alpine:3.22",
  input: { greeting: M.Greeting, count: M.LineCount },
  output: M.Summary,
  timeoutSeconds: 300,
});

/** Failure handler: logs the FailureContext it is given (proves onFailure dispatch). */
export const logFailure = step({
  name: "log-failure",
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
