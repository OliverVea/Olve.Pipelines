# Subject Index

[← Index](index.md)

Look up a subject and jump to the page (and anchor) that covers it. For agents: every link is a
relative path under `/docs/`, so `GET /docs/<target>` returns that page's raw Markdown.

## Pages at a glance

| Page | Covers |
|---|---|
| [index.md](index.md) | Overview, the GitOps mental model, minimal example, where to start |
| [getting-started.md](getting-started.md) | The 4-step setup walkthrough |
| [config-reference.md](config-reference.md) | Full `config.yaml` schema, extraction, secrets, triggers, validation |
| [examples.md](examples.md) | Four real projects mapped to capabilities; what composability the model needs next |
| [binding-and-reconcile.md](binding-and-reconcile.md) | Binding commands (`pl binding …`), the reconcile loop, status, git-only restriction |
| [bundles-and-execution.md](bundles-and-execution.md) | K8s Jobs, ArtifactBundle, scheduling, the production→processing flow |
| [promotion-gate.md](promotion-gate.md) | Brake + re-promote; operational state vs config |
| [script-library.md](script-library.md) | `olve-lib.sh` shared shell helpers; how a step sources it; function reference |
| [troubleshooting.md](troubleshooting.md) | Symptoms → cause → fix |

## Subjects A–Z

| Subject | Where |
|---|---|
| `apiVersion` (compatibility, format) | [config-reference.md#apiversion](config-reference.md#apiversion) |
| ArtifactBundle (what it is, layout) | [bundles-and-execution.md#artifactbundle](bundles-and-execution.md#artifactbundle) |
| Beta-gates-prod pattern | [bundles-and-execution.md#processing--sequential-consumes-the-bundle](bundles-and-execution.md#processing--sequential-consumes-the-bundle) |
| Binding a pipeline to a repo | [binding-and-reconcile.md#binding-commands](binding-and-reconcile.md#binding-commands) |
| Binding commands (table) | [binding-and-reconcile.md#binding-commands](binding-and-reconcile.md#binding-commands) |
| Branch (default, watching) | [binding-and-reconcile.md#the-binding](binding-and-reconcile.md#the-binding) |
| Brake (block/unblock promotion) | [promotion-gate.md](promotion-gate.md) |
| Build → deploy flow (end to end) | [bundles-and-execution.md#how-a-build-flows-end-to-end](bundles-and-execution.md#how-a-build-flows-end-to-end) |
| config-before-build guarantee | [binding-and-reconcile.md#config-before-build](binding-and-reconcile.md#config-before-build) |
| `config.yaml` full schema | [config-reference.md#top-level-schema](config-reference.md#top-level-schema) |
| `credentialsSecret` (repo read token) | [binding-and-reconcile.md#the-binding](binding-and-reconcile.md#the-binding) · [troubleshooting.md](troubleshooting.md#private-repo-the-fetch-fails--branch-head-4xx) |
| Deploy poll (implicit, why no trigger) | [getting-started.md#step-2--create-a-pipeline-bound-to-your-repo](getting-started.md#step-2--create-a-pipeline-bound-to-your-repo) · [binding-and-reconcile.md#the-binding](binding-and-reconcile.md#the-binding) |
| Drain (removed steps removed from pipeline) | [binding-and-reconcile.md#the-reconcile-loop](binding-and-reconcile.md#the-reconcile-loop) |
| Duplicate step name (error) | [config-reference.md#validation-rules-reconcile-rejects-on-any-of-these](config-reference.md#validation-rules-reconcile-rejects-on-any-of-these) |
| Environment variables (step config) | [config-reference.md#step-object](config-reference.md#step-object) |
| ETag / conditional fetch / 304 | [binding-and-reconcile.md#the-reconcile-loop](binding-and-reconcile.md#the-reconcile-loop) |
| Getting started (4 steps) | [getting-started.md](getting-started.md) |
| Git-only (rejected config endpoints) | [binding-and-reconcile.md#git-only-there-are-no-config-mutation-endpoints](binding-and-reconcile.md#git-only-there-are-no-config-mutation-endpoints) |
| GitOps model (overview) | [index.md#how-you-configure-it-gitops-only](index.md#how-you-configure-it-gitops-only) |
| `image` (step config) | [config-reference.md#step-object](config-reference.md#step-object) |
| Job statuses | [bundles-and-execution.md#jobs-and-scheduling](bundles-and-execution.md#jobs-and-scheduling) |
| Kaniko build helper (`olve_kaniko_build`) | [script-library.md#functions](script-library.md#functions) |
| Job commands (cancel, queue, get, logs) | [bundles-and-execution.md#job-commands](bundles-and-execution.md#job-commands) |
| Kubernetes Jobs (how steps run) | [bundles-and-execution.md#every-step-is-a-kubernetes-job](bundles-and-execution.md#every-step-is-a-kubernetes-job) |
| Latest-wins scheduling | [bundles-and-execution.md#jobs-and-scheduling](bundles-and-execution.md#jobs-and-scheduling) · [troubleshooting.md](troubleshooting.md#a-new-job-seems-to-have-replaced-my-queued-one) |
| Minimal config example | [index.md#minimal-example](index.md#minimal-example) |
| `name` (required) | [config-reference.md#name](config-reference.md#name) |
| `path` (config directory) | [binding-and-reconcile.md#the-binding](binding-and-reconcile.md#the-binding) |
| Poll trigger (URL, valuePath, headers) | [config-reference.md#triggers](config-reference.md#triggers) |
| Processing steps (sequential) | [bundles-and-execution.md#processing--sequential-consumes-the-bundle](bundles-and-execution.md#processing--sequential-consumes-the-bundle) |
| Production steps (parallel) | [bundles-and-execution.md#production--parallel-produces-the-bundle](bundles-and-execution.md#production--parallel-produces-the-bundle) |
| Promotion gate (state, not config) | [promotion-gate.md](promotion-gate.md) |
| `$ref` (extract a step to a file) | [config-reference.md#ref--pull-a-step-from-its-own-file](config-reference.md#ref--pull-a-step-from-its-own-file) |
| Reconcile loop (steps 1–6) | [binding-and-reconcile.md#the-reconcile-loop](binding-and-reconcile.md#the-reconcile-loop) |
| Reconcile result (NeverRun/Success/Error) | [binding-and-reconcile.md#reconcile-result](binding-and-reconcile.md#reconcile-result) |
| Re-promote (redrive last bundle) | [promotion-gate.md](promotion-gate.md) |
| `script` vs `scriptFile` | [config-reference.md#scriptfile--keep-a-script-out-of-line](config-reference.md#scriptfile--keep-a-script-out-of-line) |
| Script library (`olve-lib.sh`, sourcing it) | [script-library.md](script-library.md) |
| Shared shell functions (deploy/build) | [script-library.md#functions](script-library.md#functions) |
| Secrets (declare by name) | [config-reference.md#secrets](config-reference.md#secrets) |
| Secrets (set values with `pl secret set`) | [getting-started.md#step-3--set-the-secret-values](getting-started.md#step-3--set-the-secret-values) |
| `$SECRET:NAME` reference | [config-reference.md#secrets](config-reference.md#secrets) |
| Secret status (set/unset/unknown) | [binding-and-reconcile.md#secret-state](binding-and-reconcile.md#secret-state) · [troubleshooting.md](troubleshooting.md#a-secret-shows-unknown) |
| Binding status (`pl binding status`) | [binding-and-reconcile.md#reading-the-status](binding-and-reconcile.md#reading-the-status) |
| Step configuration (image/script/env) | [config-reference.md#step-object](config-reference.md#step-object) |
| Triggers (production/processing/poll) | [config-reference.md#triggers](config-reference.md#triggers) |
| Troubleshooting (symptom → fix) | [troubleshooting.md](troubleshooting.md) |
| Validation rules (full table) | [config-reference.md#validation-rules-reconcile-rejects-on-any-of-these](config-reference.md#validation-rules-reconcile-rejects-on-any-of-these) |
| Worked example (this repo) | [config-reference.md#worked-example--this-repos-own-config](config-reference.md#worked-example--this-repos-own-config) |
