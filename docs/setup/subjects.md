# Subject Index

[← Index](index.md)

Look up a subject and jump to the page (and anchor) that covers it. For agents: every link is a
relative path under `/docs/`, so `GET /docs/<target>` returns that page's raw Markdown.

## Pages at a glance

| Page | Covers |
|---|---|
| [index.md](index.md) | Overview, concepts, GitOps model, minimal example, real pipelines to copy |
| [getting-started.md](getting-started.md) | Put a repo under CD: `.pipelines/`, local check, bind, secrets, first run |
| [pipeline-reference.md](pipeline-reference.md) | Models, secrets, steps, library steps, handlers, stages, mappers, `when`, triggers, `onFailure`, checks, versioning |
| [local-development.md](local-development.md) | `pl dev describe \| validate \| run \| pipeline`, `--docker`, the local pool, `testHandler` |
| [binding-and-reconcile.md](binding-and-reconcile.md) | Binding, how changes are noticed, the reconcile cycle, versions, switch/rollback, status |
| [runs-and-operations.md](runs-and-operations.md) | Runs, attempts, scheduling, supersede, de-dup, rerun/continue/cancel, addresses, how a step runs, storage, retention |
| [blocking-steps.md](blocking-steps.md) | Blocking a step, held runs |
| [troubleshooting.md](troubleshooting.md) | Symptoms → cause → fix |

## Subjects A–Z

| Subject | Where |
|---|---|
| Addresses (`<pipeline>#<run>/<step>@<attempt>`) | [runs-and-operations.md#addresses](runs-and-operations.md#addresses) |
| Attempts (statuses, one K8s Job each) | [runs-and-operations.md#runs-and-attempts](runs-and-operations.md#runs-and-attempts) |
| Binding a pipeline to a repo | [binding-and-reconcile.md#the-binding](binding-and-reconcile.md#the-binding) |
| Build Job (compile + describe at reconcile) | [binding-and-reconcile.md#the-reconcile-cycle](binding-and-reconcile.md#the-reconcile-cycle) |
| Bun, `bun.lock`, `package.json` | [getting-started.md#step-1--add-pipelines-to-your-repo](getting-started.md#step-1--add-pipelines-to-your-repo) |
| Cancel a run | [runs-and-operations.md#operating-runs](runs-and-operations.md#operating-runs) |
| Checks that reject a pipeline | [pipeline-reference.md#checks-that-reject-a-pipeline](pipeline-reference.md#checks-that-reject-a-pipeline) |
| Config-before-run | [binding-and-reconcile.md#the-reconcile-cycle](binding-and-reconcile.md#the-reconcile-cycle) |
| Continue a run (promote what you tested) | [runs-and-operations.md#operating-runs](runs-and-operations.md#operating-runs) |
| `ctx` (handler context) | [pipeline-reference.md#handlers](pipeline-reference.md#handlers) |
| De-duplication of automatic triggers | [runs-and-operations.md#scheduling-rules](runs-and-operations.md#scheduling-rules) |
| Webhook vs polling | [binding-and-reconcile.md#how-changes-are-noticed](binding-and-reconcile.md#how-changes-are-noticed) |
| `--docker` (run a step in its image) | [local-development.md#3-run-one-step-in-its-real-image](local-development.md#3-run-one-step-in-its-real-image) |
| Draining | [binding-and-reconcile.md#switch-and-rollback](binding-and-reconcile.md#switch-and-rollback) |
| Exit codes (harness) | [troubleshooting.md#a-step-failed](troubleshooting.md#a-step-failed) |
| Failure handlers (`onFailure`, `FailureContext`) | [pipeline-reference.md#failure-handlers](pipeline-reference.md#failure-handlers) |
| File references (`fileRef`) | [pipeline-reference.md#models](pipeline-reference.md#models) |
| Files, outputs, logs of a step | [runs-and-operations.md#operating-runs](runs-and-operations.md#operating-runs) |
| Blocking a step (block / unblock, held runs) | [blocking-steps.md](blocking-steps.md) |
| Git-only (no shape-mutation API) | [binding-and-reconcile.md#git-only](binding-and-reconcile.md#git-only) |
| Handlers | [pipeline-reference.md#handlers](pipeline-reference.md#handlers) |
| Harness (how a step runs in any image) | [runs-and-operations.md#how-a-step-runs](runs-and-operations.md#how-a-step-runs) |
| Health statuses | [runs-and-operations.md#operating-runs](runs-and-operations.md#operating-runs) |
| Kaniko hang ("Unpacking rootfs") | [troubleshooting.md#a-run-is-stuck](troubleshooting.md#a-run-is-stuck) |
| Latest-wins, one attempt in progress per step | [runs-and-operations.md#scheduling-rules](runs-and-operations.md#scheduling-rules) |
| Library steps (`script`, `gitHubSource`, `imageBuild`, `helmDeployOverSsh`) | [pipeline-reference.md#library-steps](pipeline-reference.md#library-steps) |
| Local pool (`.olve/pool.json`) | [local-development.md#2-run-one-step-from-source](local-development.md#2-run-one-step-from-source) |
| Login (`pl login`, v2 instance URL) | [getting-started.md#prerequisites](getting-started.md#prerequisites) |
| Mappers (`[step, from => …]`) | [pipeline-reference.md#input-matching-and-mappers](pipeline-reference.md#input-matching-and-mappers) |
| Models (nominal, strict outputs, input/output views) | [pipeline-reference.md#models](pipeline-reference.md#models) |
| `optional(Model)`, skip propagation | [pipeline-reference.md#when-blocks](pipeline-reference.md#when-blocks) |
| `pl` v2 CLI (build from the branch) | [getting-started.md#prerequisites](getting-started.md#prerequisites) |
| Poll trigger (`onPoll`, `$SECRET:` headers) | [pipeline-reference.md#triggers](pipeline-reference.md#triggers) |
| Private repo (token key) | [getting-started.md#step-3--create-a-pipeline-bound-to-your-repo](getting-started.md#step-3--create-a-pipeline-bound-to-your-repo) |
| Push trigger (`onPush`, event fields) | [pipeline-reference.md#triggers](pipeline-reference.md#triggers) |
| Reconcile `Error` | [troubleshooting.md#the-reconcile-shows-error](troubleshooting.md#the-reconcile-shows-error) |
| Rerun a run / a step | [runs-and-operations.md#operating-runs](runs-and-operations.md#operating-runs) |
| Retention | [runs-and-operations.md#retention](runs-and-operations.md#retention) |
| Rollback (`pl pipeline switch`) | [binding-and-reconcile.md#switch-and-rollback](binding-and-reconcile.md#switch-and-rollback) |
| Run plan (UI preview of `when`) | [pipeline-reference.md#when-blocks](pipeline-reference.md#when-blocks) |
| SDK on npm (`@olivervea/pipelines`, alias) | [getting-started.md#the-sdk](getting-started.md#the-sdk) |
| Secrets (declare, set, per-step mounting) | [pipeline-reference.md#secrets](pipeline-reference.md#secrets), [getting-started.md#step-4--set-the-secret-values](getting-started.md#step-4--set-the-secret-values) |
| Secret state (`set` / `unset` / `unknown`) | [binding-and-reconcile.md#reading-the-status](binding-and-reconcile.md#reading-the-status) |
| Stages and barriers | [pipeline-reference.md#stages](pipeline-reference.md#stages) |
| Status output (`pl binding status`) | [binding-and-reconcile.md#reading-the-status](binding-and-reconcile.md#reading-the-status) |
| Storage layout (Garage) | [runs-and-operations.md#storage](runs-and-operations.md#storage) |
| Supersede | [runs-and-operations.md#scheduling-rules](runs-and-operations.md#scheduling-rules) |
| Switch (version) | [binding-and-reconcile.md#switch-and-rollback](binding-and-reconcile.md#switch-and-rollback) |
| Timeouts (`timeoutSeconds`) | [pipeline-reference.md#steps](pipeline-reference.md#steps) |
| Trigger a run manually (`pl trigger`) | [runs-and-operations.md#operating-runs](runs-and-operations.md#operating-runs) |
| Versions (pipeline, step, model) | [binding-and-reconcile.md#versions](binding-and-reconcile.md#versions), [pipeline-reference.md#what-makes-a-new-version](pipeline-reference.md#what-makes-a-new-version) |
| Version stamp (`yyyy.MM.dd.<run>`) | [runs-and-operations.md#runs-and-attempts](runs-and-operations.md#runs-and-attempts) |
| `when` blocks | [pipeline-reference.md#when-blocks](pipeline-reference.md#when-blocks) |
