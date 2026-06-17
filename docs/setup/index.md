# Olve.Pipelines — Setup Guide

> **Served live at `/docs/index.md`.** These pages are raw Markdown, served by the
> Olve.Pipelines server itself so an agent (or a human) can fetch and read them without
> cloning the repo. For the in-repo developer/architecture reference, see the
> [`README.md`](https://github.com/OliverVea/Olve.Pipelines/blob/main/README.md) instead —
> these docs are the *operator/setup* view; the README is the *codebase* view.

## What this service does

Olve.Pipelines is a lightweight CD orchestration service. You point it at **your** Git
repository, and it builds and deploys your app as Kubernetes Jobs whenever you push.

A pipeline has two phases:

```
Production [N steps, parallel] ──(ArtifactBundle)──> Processing 1 ──> ... ──> Processing N [sequential]
```

- **Production steps** run in **parallel**. Each writes its output to `bundle/<step-name>/`.
  The combined output is an **ArtifactBundle**.
- **Processing steps** run **sequentially** (list order). Each receives the full ArtifactBundle.
- Every step is `(image, script, env)` and runs as a Kubernetes Job.

## How you configure it: GitOps only

**There is no imperative "create steps via the API" path.** You do not build a pipeline by
POSTing steps one at a time. Instead:

1. You add a single file — `.pipelines/config.yaml` — to **your** repository.
2. You bind a pipeline to your repo (one API call).
3. The server polls your branch (~5 min) and, on every change to the config, **reconciles**
   the live pipeline to match the file, then runs the build.

Your repo is the **single source of truth** for the pipeline's shape. A bound pipeline
*rejects* API calls that would mutate its config — only your committed file writes config.
(Operational actions — manual trigger, job cancel, setting secret *values*, the promotion
gate — stay open.)

## Start here

Read in this order:

1. **[Getting Started](getting-started.md)** — the four steps to put a repo under CD. Start here.
2. **[`config.yaml` Reference](config-reference.md)** — every field, `$ref`/`scriptFile`
   extraction, secret declarations, triggers, and the exact validation rules.
3. **[Binding & Reconcile](binding-and-reconcile.md)** — the binding API, the reconcile loop,
   the status endpoint, and the config-before-build guarantee.
4. **[Bundles & Execution](bundles-and-execution.md)** — how steps run as K8s Jobs, what an
   ArtifactBundle is, and how output flows from production to processing.
5. **[Promotion Gate](promotion-gate.md)** — operational control (brake + re-promote) over
   processing steps, which is *state, not config*.
6. **[Troubleshooting](troubleshooting.md)** — symptoms → causes → fixes.

See **[Shared Script Library](script-library.md)** to stop re-vendoring the Kaniko-build and
SSH/Helm-deploy shell in every step — `olve-lib.sh` captures those footguns once.

See **[Examples](examples.md)** for four real projects (this repo, QuestionBank, a homelab, a
game) mapped to the capability each exercises.

**Looking for a specific subject?** The **[Subject Index](subjects.md)** maps topics
(secrets, poll trigger, ETag, reconcile failure, …) to the exact page and anchor.

## Minimal example

The smallest useful `.pipelines/config.yaml` — build with Kaniko, deploy with a script:

```yaml
apiVersion: "0.0"
name: my-app

secrets:
  - name: GITHUB_TOKEN
    description: Read token to fetch the repo tarball.

productionSteps:
  - name: build
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh

processingSteps:
  - name: deploy
    configuration:
      image: alpine:latest
      script: |
        echo deploying...
```

Then bind it:

```http
POST /api/pipelines/with-repo
Content-Type: application/json

{ "name": "my-app", "repo": "you/my-app", "branch": "main",
  "path": ".pipelines", "credentialsSecret": "GITHUB_TOKEN" }
```

Set the secret value, push, and watch `GET /api/pipelines/{id}/binding/status`.
Full walkthrough: **[Getting Started](getting-started.md)**.

---

*Starter template:* [`Olve.Template.Api`](https://github.com/OliverVea/Olve.Template.Api)
ships a copy-me [`.pipelines/`](https://github.com/OliverVea/Olve.Template.Api/tree/main/.pipelines)
(Kaniko build + Helm deploy).
