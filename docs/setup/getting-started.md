# Getting Started — put a repo under CD

[← Index](index.md) · [Subject Index](subjects.md)

This is the end-to-end path: from a repo with no CD to a pipeline that builds and deploys on
every push. It is entirely **GitOps** — you author one file in your repo and make one binding
call; you never author steps or triggers through the API.

## Prerequisites

- A Git repository on GitHub the server can read (public, or private with a read token).
- The container images your steps need (e.g. a Kaniko build image, an `alpine` deploy image).
- Access to the Olve.Pipelines API.

## Step 1 — Add `.pipelines/config.yaml` to your repo

Create `.pipelines/config.yaml` at the root of your repository. This file is the **single
source of truth** for the pipeline's shape.

```yaml
apiVersion: "0.0"            # major must match the server (currently 0.x)
name: my-app

secrets:                     # declared by NAME ONLY — values never live in the repo
  - name: GITHUB_TOKEN
    description: Read token to fetch the repo tarball during build.

productionSteps:             # run in parallel → bundle/<step-name>/
  - name: build
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh    # keep the script out-of-line under .pipelines/scripts/

processingSteps:             # run sequentially (order = list position)
  - name: deploy
    configuration:
      image: alpine:latest
      script: |
        echo "deploying $VERSION"
```

You can inline the script with `script: |` or extract it to `.pipelines/scripts/build.sh` and
reference it with `scriptFile:` (the two are mutually exclusive). See the
[`config.yaml` Reference](config-reference.md) for every field and the extraction rules.

> **Don't declare a deploy trigger.** Binding configures the deploy poll for you — pushing a
> commit deploys automatically. The `triggers:` block is optional and additive; you only need
> it for extra triggers (e.g. polling an upstream version). See
> [Triggers](config-reference.md#triggers).

## Step 2 — Create a pipeline bound to your repo

One call creates the pipeline and binds it. If the bind fails, the draft pipeline is rolled
back, so you never get an orphan.

```http
POST /api/pipelines/with-repo
Content-Type: application/json

{
  "name": "my-app",
  "repo": "you/my-app",
  "branch": "main",            // optional, defaults to "main"
  "path": ".pipelines",        // optional, defaults to ".pipelines"
  "credentialsSecret": "GITHUB_TOKEN"   // key in the pipeline's k8s secret holding the repo read token; omit for a public repo
}
```

The response is the binding. Note the pipeline `id` — you need it for the next steps.

> `with-repo` is the only way to create a pipeline: every pipeline is bound to a repo from
> birth, so its shape always comes from git. See [Binding & Reconcile](binding-and-reconcile.md).

## Step 3 — Set the secret values

Your config declares secrets **by name only**. Their *values* live in the pipeline's own
Kubernetes secret (`olve-pipeline-{id}`) and are mounted into every job — they never pass
through the repo or the app at runtime. Set each declared secret's value:

```http
PUT /api/pipelines/{id}/secrets/GITHUB_TOKEN
Content-Type: application/json

"ghp_xxxxxxxxxxxxxxxxxxxx"
```

This is an **operational** endpoint, so it works even though the pipeline is git-bound.
Setting values is the one piece of config that *can't* live in the repo, by design.

## Step 4 — Push, and watch the reconcile

Push a commit. Within ~5 minutes the server notices the branch moved, fetches your
`.pipelines/` subtree, compiles + validates the config, reconciles the live pipeline to match,
and runs the build. Check the result:

```http
GET /api/pipelines/{id}/binding/status
```

```jsonc
{
  "result": "Success",            // NeverRun | Success | Error
  "lastSyncTime": "2026-06-16T…",
  "problems": [],                 // validation/fetch problems if result == Error
  "secrets": [                    // each declared secret + whether it's set in k8s right now
    { "name": "GITHUB_TOKEN", "isSet": true }
  ],
  "lastSyncedSha": "abc123…",
  "lastDeployedSha": "abc123…"
}
```

- `result: Success` and `problems: []` → your config applied and the build ran.
- `result: Error` → live state is **unchanged**; read `problems` and fix the config. See
  [Troubleshooting](troubleshooting.md).
- An `isSet: false` secret → set its value (Step 3). `isSet: null` means k8s couldn't be read,
  not that the secret is missing.

## What happens on every subsequent push

1. The deploy poll sees the branch head move.
2. If `.pipelines/` changed, reconcile re-materializes steps/triggers to match the file.
3. The build runs; the ArtifactBundle flows through your processing steps in order.

A push that **only** touches code (not `.pipelines/`) still triggers a build — but skips the
reconcile (nothing to re-materialize). A **broken** config holds off the build for that cycle,
so bad config never ships on stale code. See
[config-before-build](binding-and-reconcile.md#config-before-build).

## Next

- Tune the file → **[`config.yaml` Reference](config-reference.md)**
- Understand the loop → **[Binding & Reconcile](binding-and-reconcile.md)**
- Pause/redrive a deploy → **[Promotion Gate](promotion-gate.md)**
- Something failed → **[Troubleshooting](troubleshooting.md)**
