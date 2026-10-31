# Olve.Pipelines — Setup Guide

> **Served live at `/docs/index.md`.** These pages are raw Markdown, served by the
> Olve.Pipelines server itself so an agent (or a human) can fetch and read them without
> cloning the repo. They are the *operator/setup* view. The *codebase* view is the repo's
> [`README.md`](https://github.com/OliverVea/Olve.Pipelines/blob/main/README.md). The SDK reference
> (every `ctx` helper and the harness contract) is
> [`src/sdk/README.md`](https://github.com/OliverVea/Olve.Pipelines/blob/main/src/sdk/README.md).

> **Instances:** `https://pipelines-private.ovea.pro` (prod) and
> `https://pipelines-beta.ovea.pro` (beta), over Tailscale.

## What this service does

Olve.Pipelines is a small CD orchestration service for a homelab. You bind a pipeline to a Git
repository. The pipeline is defined in TypeScript in that repo (`.pipelines/` by default). On every
push the service builds the definition, then runs it. Each step runs as a Kubernetes Job.

A pipeline is a list of **stages**. Stages run one after another; the steps inside a stage run in
parallel. Each step declares the typed **models** it consumes and the model it produces, so wiring
mistakes are compile errors and a bad output fails the step that produced it.

```
source ──> build ∥ test ──> when(deployTo = beta): deploy-beta
```

| Concept | What it is |
|---|---|
| **Model** | A named zod schema (`BuildArtifact`). Nominal: two models with the same shape are not interchangeable. |
| **Step** | A contract: name, image, inputs, output model, secrets. No behaviour. |
| **Handler** | The implementation of one step: `implement(step, fn)`. The result is staged like any step. |
| **Library step** | An SDK step with a built-in handler and typed config (`steps.gitHubSource`, `steps.imageBuild`, …). |
| **Pipeline** | Input model, triggers, stages, `when` blocks, failure handlers. |
| **Version** | An immutable snapshot of the pipeline, added whenever the definition changes. One version is active at a time. |
| **Run** | One execution of one version: its input plus every step's output. Numbered per pipeline (`my-app#12`). |
| **Attempt** | One execution of one step within a run (one K8s Job). Reruns add attempts. |

## How you configure it: GitOps only

1. Add `.pipelines/` to **your** repository: an ordinary TypeScript package, with `main` in its
   `package.json` naming the entry point.
2. Bind a pipeline to the repo with `pl binding create`. That is the only way to create a pipeline.
3. Set the secret values the pipeline declares with `pl secret set`.
4. Push. The service compiles the pipeline in a sandboxed build Job, validates it, adds a version,
   switches to it, and starts a run through the pipeline's `onPush` trigger.

Your repo is the only writer of pipeline shape. Operations — triggering, cancelling, rerunning,
continuing, blocking a step, switching versions, setting secret values — go through `pl` or the UI.

## Start here

1. **[Getting Started](getting-started.md)** — put a repo under CD, end to end.
2. **[Pipeline Reference](pipeline-reference.md)** — models, steps, library steps, handlers,
   stages, mappers, `when`, triggers, `onFailure`, and the checks that reject a pipeline.
3. **[Local Development](local-development.md)** — `pl dev describe | validate | run | pipeline`:
   run steps from source before you push.
4. **[Binding, Reconcile & Versions](binding-and-reconcile.md)** — the binding, how changes are noticed
   modes, how a commit becomes a version, `switch` and rollback, reading the status.
5. **[Pipeline Config](configuration.md)** — typed per-instance values (`config(…)`), the YAML
   document and its snapshots, pending versions, `pl config`.
6. **[Runs & Operations](runs-and-operations.md)** — runs, attempts, latest-wins, de-duplication,
   rerun and continue, outputs and files, how a step runs, retention.
7. **[The `pl` CLI](cli.md)** — installing `pl`, profiles, every command.
8. **The API** — the contract (`src/spec/*.tsp`) describes every operation, its auth, the error
   model and the event stream; an instance serves it as OpenAPI at `/docs/openapi.json`.
9. **[Running an Instance](self-hosting.md)** — server configuration, logging, a local instance
   with Docker Compose.
10. **[Deploying to Docker Compose](compose-deploy.md)** — the compose deployer, `deployTarget()`
   and `steps.composeDeploy`.
11. **[Blocking Steps](blocking-steps.md)** — hold a pipeline's runs at a step while you work on
   the pipeline or its target (`pl step block`), and let them resume.
12. **[Troubleshooting](troubleshooting.md)** — symptoms → causes → fixes.

**Looking for a specific subject?** The **[Subject Index](subjects.md)** maps topics to pages.

## Minimal example

A pipeline that checks out the pushed commit and runs a script against it:

```ts
// .pipelines/models.ts
import { model, z } from "@olivervea/pipelines";

export const Request = model("Request", z.object({
  repo: z.string().default("you/my-app"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
}));
```

```ts
// .pipelines/pipeline.ts
import { pipeline, steps } from "@olivervea/pipelines";
import * as M from "./models";
import { test } from "./steps";

const source = steps.gitHubSource({ name: "source", description: "Fetches the pushed commit." });

export default pipeline({ name: "my-app", input: M.Request })
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(test);
```

```ts
// .pipelines/steps.ts
import { implement, Source, step } from "@olivervea/pipelines";
import testSh from "./scripts/test.sh" with { type: "file" };

export const test = implement(
  step({ name: "test", description: "Runs the tests.", image: "alpine:3.22", input: { source: Source } }),
  async ({ source }, ctx) => {
    await ctx.sh({ script: testSh }, { cwd: await ctx.files.fetch(source.tree) });
  },
);
```

Then:

```sh
pl binding create you/my-app            # prints the binding, including the pipeline id
pl binding status <pipeline>            # reconcile result, resulting version, problems
```

Full walkthrough, including the `package.json` and the current SDK limitation for repos other
than Olve.Pipelines: **[Getting Started](getting-started.md)**.

## Real pipelines to copy from

- [`.pipelines/`](https://github.com/OliverVea/Olve.Pipelines/tree/main/.pipelines) — Olve.Pipelines
  deploys itself, driven by config (`config.ts`): `gitHubSource` → custom `compile-server` (Native
  AOT or JIT, per `build.runtime`), `build-frontend`, `test`, `build-sdk`, `build-cli` ∥ `imageBuild` (harness) → custom `build-server` (crane puts the server and
  frontend layers on the .NET base image; no Dockerfile build), `publish-sdk`, `publish-cli` →
  `select` on `target.beta.kind`: `helmDeployOverSsh` or `helmDeployInCluster` then custom
  `beta-test`, `composeDeploy`, or nothing (`none`), each next to `publish-images` → when
  `deployTo: "prod"`: the same select for prod (without `none`) → custom `promote`.
- [`.pipelines-test/`](https://github.com/OliverVea/Olve.Pipelines/tree/main/.pipelines-test) — the
  v2 fixture: file refs between steps, a parallel stage, `when` and `select` blocks, steps waiting
  for a callback and a poll input, and a failure handler. The deployer's `beta-test` reconciles and
  runs it on beta at every commit.
