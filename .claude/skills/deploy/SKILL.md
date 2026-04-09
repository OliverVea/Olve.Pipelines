---
name: deploy
description: Trigger the self-deploy pipeline for Olve.Pipelines and monitor job progress until completion.
allowed-tools: Bash Read
---

# Deploy Olve.Pipelines

Triggers the self-deploy pipeline on the production instance and monitors until completion.

## Prerequisites

The pipeline must already be configured on the running instance. If the app was restarted, run `/setup-pipeline` first.

## Steps

### 1. Check if pipeline exists

```bash
curl -sk "https://pipelines-private.ovea.pro/api/pipelines"
```

If empty (`[]`), the app was restarted and pipeline config was lost. Tell the user to run `/setup-pipeline` first and stop.

### 2. Get the webhook trigger

```bash
# Get the pipeline ID (should be only one pipeline named "olve-pipelines")
PID=$(curl -sk "https://pipelines-private.ovea.pro/api/pipelines" | uv run python -c "import sys,json; ps=json.load(sys.stdin); print(ps[0]['id'], end='')")

# Get the trigger
TOKEN=$(curl -sk -X POST "https://auth.ovea.pro/application/o/token/" \
  -d "grant_type=client_credentials" \
  -d "client_id=olve-pipelines" \
  -d "client_secret=d178464f2442ec91434117c488e1f70706ed03458634c4cace376d998bc59020" \
  -d "scope=openid" | uv run python -c "import sys,json; print(json.load(sys.stdin)['access_token'], end='')") && \
TRIGGER=$(curl -sk "https://pipelines-private.ovea.pro/api/pipelines/$PID/triggers" \
  -H "Authorization: Bearer $TOKEN") && \
TRIGGER_ID=$(echo "$TRIGGER" | uv run python -c "import sys,json; ts=json.load(sys.stdin); print(ts[0]['id'], end='')") && \
TRIGGER_SECRET=$(echo "$TRIGGER" | uv run python -c "import sys,json; ts=json.load(sys.stdin); print(ts[0]['secret'], end='')")
```

### 3. Fire the trigger

```bash
curl -sk -X POST "https://pipelines-private.ovea.pro/api/webhooks/$TRIGGER_ID" \
  -H "Authorization: Bearer $TRIGGER_SECRET"
```

### 4. Monitor jobs

Poll every 30 seconds. The production step (Kaniko build) takes ~3-5 minutes. The processing step (deploy) takes ~1-2 minutes.

```bash
curl -sk "https://pipelines-private.ovea.pro/api/jobs" | uv run python -c "
import sys, json
jobs = json.load(sys.stdin)
for j in sorted(jobs, key=lambda x: x['createdAt']):
    pid = j['pipelineId']
    print(f\"{j['id'][:8]}... | {j['\$type']} | {j['status']['\$type']}\")"
```

Terminal statuses: `done`, `failed`, `cancelled`, `obsolete`.

### 5. On failure, check logs

```bash
curl -sk "https://pipelines-private.ovea.pro/api/jobs/$JOB_ID/logs"
```

Also check app logs:
```bash
ssh oliver@bulwark-m2 "kubectl logs deploy/olve-pipelines -n apps --since=10m 2>&1 | grep -v health"
```

### 6. Verify deployment

After all jobs reach `done`:
```bash
ssh oliver@bulwark-m2 "kubectl get pods -n apps"
curl -sk "https://pipelines-private.ovea.pro/api/health"
```

## Expected flow

```
production (build-and-package) ~3-5 min
  → processing (deploy) ~1-2 min
    → app restarts with new image
```

**Note:** Once the persistence fix (save-on-mutate) is deployed, pipeline config survives restarts — no need to run `/setup-pipeline` after every deploy. Until then, you'll need to run `/setup-pipeline` again before the next deploy.
