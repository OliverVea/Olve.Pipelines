# Local Development (`pl dev`)

[← Index](index.md) · [Subject Index](subjects.md)

`pl dev` runs a v2 pipeline from your checkout: no service, no login (except `pl dev validate`), no
push. It shells out to the SDK's `olve-dev` / `olve-build` through Bun.

- **Bun** is found via `$OLVE_BUN`, then `PATH`, then `~/.bun/bin/bun`.
- **The SDK** is the `@olivervea/pipelines` your pipeline resolves: run `bun install` in the pipeline
  directory first. In another repo that is `"@olivervea/pipelines": "<version>"` from npm (see
  [Getting Started](getting-started.md#the-sdk)); in this repo it is `file:../sdk`. Override with
  `$OLVE_SDK`. Without `pl`, the same tools run as `bunx olve-dev describe .` and
  `bunx olve-build . --out .olve/out` from the pipeline directory.
- **The pipeline directory** defaults to `.pipelines`; `--dir <path>` picks another (e.g.
  `--dir .pipelines-test`).
- Everything is written to `<dir>/.olve/` — keep it git-ignored.

## 1. Describe and validate

```sh
pl dev describe [--json]       # manifest summary + describe-time checks; exit 78 on problems
pl dev validate [--json]       # describe, then the service's reconcile checks; exit 78 if invalid
```

`pl dev describe` evaluates `models.ts` and `pipeline.ts` (never handlers) exactly like the build
Job and prints the input model, secrets, triggers, stages, `when` blocks and failure handlers.
`--json` prints the canonical manifest. For this repo's fixture:

```text
$ pl dev describe --dir .pipelines-test
v2 test fixture — End-to-end fixture: file refs between steps, a parallel stage, nested and multi-field when blocks, and a config select.

input:    TestInput { mode="normal" (normal|skip-last|fail), sha*, smoke=true, target="beta" (none|beta|prod) }
secrets:  none
triggers: push, poll (GET https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/v2 every 3600s)

stages:
  1.  hello                alpine:3.22 -> Greeting
  2.  count                debian:bookworm-slim -> LineCount
      echo                 busybox:1.37
  when mode in ["normal"]:
    3.  last                 alpine:3.22 -> Summary
    when target in ["beta", "prod"]:
      4.  deploy-beta          alpine:3.22
      when target in ["prod"]:
        5.  deploy-prod          alpine:3.22
        when mode in ["normal"] and smoke in [true]:
          6.  smoke                alpine:3.22
  select on config notify.channel:
    log:
      7.  notify-log           alpine:3.22
    none:
    webhook:
      7.  notify-webhook       alpine:3.22

failure handlers:
  log-failure            on any step
```

`pl dev validate` posts the manifest to the instance you are logged in to
(`POST /api/manifests/validate`), which runs the same checks reconcile runs — nothing is persisted.
Errors exit 78; warnings alone exit 0. Run it before pushing a pipeline change.

`tsc` (`bun run typecheck` with the template `package.json`) catches wiring errors before either.

## 2. Run one step from source

```sh
pl dev run <step> --input sha=<40 hex>       # pipeline-input fields, typed by the input model
pl dev run build                             # next step: inputs come from the local pool
pl dev run checkout --input-file in.json     # use input.json verbatim
pl dev run deploy-beta                       # declared secrets come from your environment (or --secret NAME=value)
```

The step runs through the same harness path as in the cluster: validate input → handler → validate
output → `<dir>/.olve/work/`. It prints the result envelope; the exit code is the harness's (0 ok or
skipped, 1 handler threw, 65 input invalid, 66 output invalid, 78 pipeline invalid). `--json` prints
the raw envelope.

- **Local pool.** `<dir>/.olve/pool.json` holds the pipeline input and each step's latest local
  outcome. A step's `input.json` is built from its manifest mappings, the way the service builds it:
  `input.*` refs read the pipeline input (your `--input k=v` values over the pool's previous input,
  then the model's defaults); step refs read earlier steps' outputs. Output files are copied into
  `<dir>/.olve/files/` and passed as `OLVE_FILES_DIR`, so running steps in order chains them, file
  references included.
- **Missing inputs.** If a required input's step never ran, `pl dev run` fails with a hint to run it
  first. If that step was skipped, an optional input is left out.
- **Secrets.** Only the step's declared secrets reach it, from `--secret NAME=value` or your
  environment; every other declared secret is removed from the child's environment. A declared
  secret that isn't set is a warning — the handler fails only if it reads it. Secrets are never
  pulled from the cluster.

## 3. Run one step in its real image

```sh
pl dev run build --docker
```

Compiles the real binary with `olve-build` into `<dir>/.olve/build`, extracts the glibc loader libs
from the `harness-base` image (once, into `<dir>/.olve/lib/<tag>/`), stages `/olve` (`step`, `lib/`,
`input.json`, `work/`, `tmp/`) into a container of the step's declared image, and runs
`/olve/lib/ld-linux-x86-64.so.2 --library-path /olve/lib /olve/step run <step>` with
`OLVE_DIR=/olve` and `TMPDIR=/olve/tmp`, the pool's files mounted read-only. `/olve/work` is copied
back to `<dir>/.olve/work`. This catches what source runs can't: a missing shell or tool in the
image, no `/tmp` (kaniko), libc surprises. Needs Docker.

## 4. Run the whole pipeline locally

```sh
pl dev pipeline --input sha=<sha> --input deployTo=beta [--until <step>] [--only a,b] [--docker] [--yes]
```

- Runs the stages in order; the steps of a stage run one after another locally.
- Evaluates `when` blocks against the input and propagates skips. A failed step stops the run (exit =
  the harness exit code).
- Asks for confirmation before each step that declares secrets (those do real things); without a
  terminal it refuses unless `--yes`.
- A full run starts a fresh pool; `--only` reuses the pool for every step it doesn't run.
- `--until <step>` stops after the stage containing `<step>`.
- Failure handlers are not run.
- A `select` runs every case's steps: there is no config locally.

```text
$ pl dev pipeline --dir .pipelines-test --input sha=abcdef1 --input mode=skip-last
…
result:
  hello           succeeded
  count           succeeded
  echo            succeeded
  last            skipped   when mode in ["normal"] did not match (mode = "skip-last")
  deploy-beta     skipped   when mode in ["normal"] did not match (mode = "skip-last")
  deploy-prod     skipped   when mode in ["normal"] did not match (mode = "skip-last")
  smoke           skipped   when mode in ["normal"] did not match (mode = "skip-last")
  notify-log      succeeded
  notify-webhook  succeeded
```

## Unit-testing handlers

```ts
import { testHandler } from "@olivervea/pipelines/testing";
import deployBeta from "../handlers/deploy-beta";

const r = await testHandler(deployBeta, { artifact }, { secrets: { SSH_PRIVATE_KEY: "k" }, ctx: { http: fakeHttp } });
expect(r).toMatchObject({ status: "succeeded" });
```

`testHandler` uses the same validation path as a real run; replace any part of `ctx`. See
[sdk/README.md "Testing handlers"](https://github.com/OliverVea/Olve.Pipelines/blob/main/sdk/README.md#testing-handlers).

There is no "run on the cluster from a laptop": pushing to the bound branch does that.
