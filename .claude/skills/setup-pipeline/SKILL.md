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

Cut over **beta-first**. Set `API` once and the rest follows:

```bash
API=https://pipelines-private.ovea.pro   # or https://pipelines-beta.ovea.pro
```

## Step 1: Verify the app is running

```bash
curl -sk "$API/api/health"
```

If not reachable, restart it (prod shown; beta uses `-n apps-beta`):
```bash
ssh oliver@bulwark-m2 "kubectl rollout restart deploy/olve-pipelines -n apps && kubectl rollout status deploy/olve-pipelines -n apps --timeout=60s"
```

## Step 2: Confirm no pipeline exists

```bash
curl -sk "$API/api/pipelines"
```

This skill recreates from scratch. If a pipeline named `olve-pipelines` still exists, the
state you meant to delete is still there — do **not** proceed or you will create a
duplicate with a new ID, orphaning the old one.

## Step 3: Get an auth token

```bash
TOKEN=$(curl -sk -X POST "https://auth.ovea.pro/application/o/token/" \
  -d "grant_type=client_credentials" \
  -d "client_id=olve-pipelines" \
  -d "client_secret=d178464f2442ec91434117c488e1f70706ed03458634c4cace376d998bc59020" \
  -d "scope=openid" | uv run python -c "import sys,json; print(json.load(sys.stdin)['access_token'], end='')")
H="Authorization: Bearer $TOKEN"
```

## Step 4: Create the pipeline bound to the repo

One call composes pipeline + binding + deploy poll. `credentialsSecret` names the key in
the pipeline's k8s secret that holds the GitHub token used to fetch `.pipelines/` (set in
Step 5). `branch` defaults to `main`, `path` to `.pipelines`.

```bash
BINDING=$(curl -sk -X POST "$API/api/pipelines/with-repo" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{
    "name":"olve-pipelines",
    "repo":"OliverVea/Olve.Pipelines",
    "branch":"main",
    "path":".pipelines",
    "credentialsSecret":"GITHUB_TOKEN"
  }') && \
echo "$BINDING" && \
PID=$(echo "$BINDING" | uv run python -c "import sys,json; print(json.load(sys.stdin)['pipelineId'], end='')") && \
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

# Extract values from the old secret
OLD_SECRET_NAME="<name from above>"
SECRETS_JSON=$(ssh oliver@bulwark-m2 "kubectl get secret $OLD_SECRET_NAME -n olve-runners -o json | python3 -c \"import sys,json,base64; d=json.load(sys.stdin)['data']; print(json.dumps({k:base64.b64decode(v).decode() for k,v in d.items()}))\"")

GITHUB_TOKEN=$(echo "$SECRETS_JSON" | uv run python -c "import sys,json; print(json.load(sys.stdin)['GITHUB_TOKEN'], end='')")
SSH_KEY=$(echo "$SECRETS_JSON" | uv run python -c "import sys,json; print(json.load(sys.stdin)['SSH_PRIVATE_KEY'], end='')")

curl -sk -X PUT "$API/api/pipelines/$PID/secrets/GITHUB_TOKEN" \
  -H "$H" -H "Content-Type: application/json" \
  -d "{\"value\":\"$GITHUB_TOKEN\"}"

curl -sk -X PUT "$API/api/pipelines/$PID/secrets/SSH_PRIVATE_KEY" \
  -H "$H" -H "Content-Type: application/json" \
  -d "{\"value\":$(echo "$SSH_KEY" | uv run python -c "import sys,json; print(json.dumps(sys.stdin.read().strip()))")}"
```

If there is no previous secret to copy from, set `GITHUB_TOKEN` to a GitHub read token and
`SSH_PRIVATE_KEY` to the homelab deploy key out-of-band.

## Step 6: Wait for the first reconcile

The deploy poll runs on a ~5-minute cadence (`ReconcileOptions.PollInterval`). On its first
cycle it fetches `.pipelines/config.yaml`, materializes the steps, and seeds the deploy
cursor (it does **not** build on first observation). Check the binding status:

```bash
curl -sk "$API/api/pipelines/$PID/binding/status" | uv run python -m json.tool
```

Expect `result: "Success"`, both declared secrets `isSet: true`, and no `problems`. If
`result` is `Error`, the first `problems` entry says why (bad token, fetch/compile failure).
A broken config holds off the build — fix the repo and push; the next poll retries.

Confirm the steps materialized:

```bash
curl -sk "$API/api/pipelines/$PID/document" -H "$H" | uv run python -m json.tool
```

## Step 7: Verify a deploy

Push a commit to `main` (or run `/deploy` for a manual trigger). The pipeline builds, runs
`deploy-beta` (which health-gates), then `deploy`. Watch jobs via `/deploy` or the frontend
badge (repo@branch + reconcile/secret state).

## After setup

Print for the user:
- Pipeline ID and the bound repo/branch/path.
- Binding status (`result`, secrets set/unset).

Tell them deploys now happen automatically on push to `main`, config changes go through
`.pipelines/config.yaml` (git-only — API config edits are rejected on a bound pipeline), and
`/deploy` triggers a manual run.
