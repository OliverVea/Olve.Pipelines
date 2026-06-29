---
name: setup-pipeline
description: Recreate the self-deploy pipeline on the Olve.Pipelines instance after a restart by binding it to the repo (GitOps); reconcile materializes the steps from .pipelines/config.yaml.
allowed-tools: Bash Read
---

# Setup Self-Deploy Pipeline (GitOps)

This is the **bootstrap runbook** — it only creates the pipeline, binds it to the repo, and
sets secrets. The reconcile loop then materializes everything from
[`.pipelines/config.yaml`](../../../.pipelines/config.yaml) (one Kaniko build, then
`deploy-beta` gating `deploy`). For how GitOps config works and the `config.yaml` schema, see
the **GitOps Configuration** section of [`README.md`](../../../README.md).

You only need this after a hard reset (S3 state deleted or an incompatible schema migration).
To change *what* the pipeline does, edit `.pipelines/config.yaml` and push — do **not** call
config-mutation endpoints (a bound pipeline rejects them).

## Pick the environment

| Env  | API base                              | helm namespace |
|------|---------------------------------------|----------------|
| prod | `https://pipelines-private.ovea.pro`  | `apps`         |
| beta | `https://pipelines-beta.ovea.pro`     | `apps-beta`    |

Cut over **beta-first**. Export `PIPELINES_API_URL` once — every `pl` command below
(including `pl login`) reads it, so it targets this environment automatically:

```bash
export PIPELINES_API_URL=https://pipelines-private.ovea.pro   # or https://pipelines-beta.ovea.pro
```

## Step 1: Verify the app is running

```bash
curl -sk "$PIPELINES_API_URL/api/health"
```

If not reachable, restart it (prod shown; beta uses `-n apps-beta`):
```bash
ssh oliver@bulwark-m2 "kubectl rollout restart deploy/olve-pipelines -n apps && kubectl rollout status deploy/olve-pipelines -n apps --timeout=60s"
```

## Step 2: Confirm no pipeline exists

```bash
pl pipeline list
```

This skill recreates from scratch. If a pipeline named `olve-pipelines` still exists, the
state you meant to delete is still there — do **not** proceed or you will create a
duplicate with a new ID, orphaning the old one (`pl binding create` mints a fresh id each run).

## Step 3: Log in

`pl` mutations read a cached token from `~/.pl`, so authenticate once. `pl login` runs the
browser OIDC flow; over SSH / on a headless box it auto-switches to the device flow (RFC 8628
— scan the QR or open the URL on your phone), or force it with `--device`:

```bash
pl login           # add --device over SSH
```

This caches the token **and** the resolved `PIPELINES_API_URL` to `~/.pl`, so the commands
below need no token flag. (Device flow depends on the Authentik worker being healthy.)

## Step 4: Create the pipeline bound to the repo

One call composes pipeline + binding + deploy poll. `--credentials-secret` names the key in
the pipeline's k8s secret that holds the GitHub token used to fetch `.pipelines/` (set in
Step 5). `--branch` defaults to `main`, `--path` to `.pipelines`.

```bash
PID=$(pl binding create OliverVea/Olve.Pipelines \
  --credentials-secret GITHUB_TOKEN --json | jq -r .pipelineId)
echo "Pipeline: $PID"
```

## Step 5: Set pipeline secrets

Secrets are declared by name in `config.yaml` (`GITHUB_TOKEN`, `SSH_PRIVATE_KEY`) but their
values live only in the pipeline's k8s secret — set them here. **The first reconcile cannot
fetch `.pipelines/` until `GITHUB_TOKEN` is set**, so do this promptly after Step 4.

The k8s secrets survive restarts, but the pipeline ID changes each time, so copy the values
from the most recent previous pipeline's secret:

```bash
# Find the old secret (lists all pipeline secrets in olve-runners namespace)
ssh oliver@bulwark-m2 "kubectl get secrets -n olve-runners | grep olve-pipeline-"

# Copy each value straight from the old k8s secret into the new pipeline. base64 -d emits the
# exact bytes (no command substitution, so trailing newlines in PEM keys survive); pl secret
# set reads stdin verbatim.
OLD_SECRET_NAME="<name from above>"
for KEY in GITHUB_TOKEN SSH_PRIVATE_KEY; do
  ssh oliver@bulwark-m2 "kubectl get secret $OLD_SECRET_NAME -n olve-runners -o jsonpath=\"{.data.$KEY}\"" \
    | base64 -d | pl secret set "$PID" "$KEY"
done
```

If there is no previous secret to copy from, set `GITHUB_TOKEN` to a GitHub read token and
`SSH_PRIVATE_KEY` to the homelab deploy key out-of-band.

## Step 6: Wait for the first reconcile

The deploy poll runs on a ~5-minute cadence (`ReconcileOptions.PollInterval`). On its first
cycle it fetches `.pipelines/config.yaml`, materializes the steps, and seeds the deploy
cursor (it does **not** build on first observation). Check the binding status:

```bash
pl binding status "$PID"
```

Expect `Reconcile: Success`, both declared secrets `set` in the secrets table, and no
problems. If reconcile is `Error`, the first problem says why (bad token, fetch/compile
failure). A broken config holds off the build — fix the repo and push; the next poll retries.
(To apply immediately instead of waiting for the poll: `pl binding reconcile "$PID"`.)

Confirm the steps materialized:

```bash
pl pipeline document "$PID"
```

## Step 7: Verify a deploy

Push a commit to `main` (or run `/deploy` for a manual trigger). The pipeline builds, runs
`deploy-beta` (which health-gates), then `deploy`. Watch jobs via `pl job list --pipeline "$PID"`,
`/deploy`, or the frontend badge (repo@branch + reconcile/secret state).

## After setup

Print for the user:
- Pipeline ID and the bound repo/branch/path.
- Binding status (`result`, secrets set/unset).

Tell them deploys now happen automatically on push to `main`, config changes go through
`.pipelines/config.yaml` (git-only — API config edits are rejected on a bound pipeline), and
`/deploy` triggers a manual run.
