---
name: setup-pipeline
description: Recreate the self-deploy pipeline configuration on the production Olve.Pipelines instance after a restart.
allowed-tools: Bash Read
---

# Setup Self-Deploy Pipeline

Pipeline configuration now persists to S3, so this skill is only needed after a hard reset (S3 state deleted or incompatible schema migration). It recreates the full self-deploy pipeline: production step (Kaniko build), two processing steps (deploy-beta, then deploy-prod), secrets, and a poll trigger.

Beta deploys first and gates prod: if the beta step fails, the prod step never runs.

## Step 1: Verify the app is running

```bash
curl -sk "https://pipelines-private.ovea.pro/api/health"
```

If not reachable, the app may need to be restarted:
```bash
ssh oliver@bulwark-m2 "kubectl rollout restart deploy/olve-pipelines -n apps && kubectl rollout status deploy/olve-pipelines -n apps --timeout=60s"
```

## Step 2: Confirm no pipeline exists

```bash
curl -sk "https://pipelines-private.ovea.pro/api/pipelines"
```

This skill is for recreating from scratch after a reset. If a pipeline named `olve-pipelines` still exists, the state you intended to delete is still there — do not proceed or you will create a duplicate pipeline with a new ID, leaving the old one orphaned.

## Step 3: Get an auth token

```bash
TOKEN=$(curl -sk -X POST "https://auth.ovea.pro/application/o/token/" \
  -d "grant_type=client_credentials" \
  -d "client_id=olve-pipelines" \
  -d "client_secret=d178464f2442ec91434117c488e1f70706ed03458634c4cace376d998bc59020" \
  -d "scope=openid" | uv run python -c "import sys,json; print(json.load(sys.stdin)['access_token'], end='')")
```

## Step 4: Create pipeline and steps

Run this as a single chained command to capture IDs:

```bash
H="Authorization: Bearer $TOKEN" && \
PIPELINE=$(curl -sk -X POST "https://pipelines-private.ovea.pro/api/pipelines?name=olve-pipelines" -H "$H") && \
PID=$(echo "$PIPELINE" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
echo "Pipeline: $PID" && \
PROD=$(curl -sk -X POST "https://pipelines-private.ovea.pro/api/pipelines/$PID/production" \
  -H "$H" -H "Content-Type: application/json" -d '{"name":"build-and-package"}') && \
PROD_ID=$(echo "$PROD" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
echo "Production step: $PROD_ID" && \
BETA=$(curl -sk -X POST "https://pipelines-private.ovea.pro/api/pipelines/$PID/processing" \
  -H "$H" -H "Content-Type: application/json" -d '{"name":"deploy-beta","order":0}') && \
BETA_ID=$(echo "$BETA" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
echo "Processing step (beta): $BETA_ID" && \
PROC=$(curl -sk -X POST "https://pipelines-private.ovea.pro/api/pipelines/$PID/processing" \
  -H "$H" -H "Content-Type: application/json" -d '{"name":"deploy","order":1}') && \
PROC_ID=$(echo "$PROC" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
echo "Processing step (prod): $PROC_ID"
```

## Step 5: Configure production step

Key points about this script:
- Uses BusyBox wget with `--no-check-certificate` (Kaniko debug image limitation)
- Build context lives at `/kaniko/build-context`, NOT `/workspace` — Kaniko wipes `/` (including `/workspace`) between multi-stage builds, which broke stage 2's `COPY clients/...`. The `/kaniko` dir is preserved across stage transitions.
- Does NOT pass `--single-snapshot` — it's incompatible with the multi-stage Dockerfile (stage 2 needs the context intact).
- Copies helm/ and version.txt to /output/ before running Kaniko so artifacts survive
- Builds from `main` branch

```bash
curl -sk -X PUT "https://pipelines-private.ovea.pro/api/production-steps/$PROD_ID/configuration" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{
  "image": "gcr.io/kaniko-project/executor:debug",
  "script": "set -e\nREPO=OliverVea/Olve.Pipelines\nBRANCH=main\nVERSION=$(date +%Y%m%d-%H%M%S)\n\nCTX=/kaniko/build-context\nmkdir -p $CTX\ncd $CTX\n\n# Download repo tarball (busybox wget needs --no-check-certificate)\nwget --no-check-certificate -q --header=\"Authorization: token $GITHUB_TOKEN\" -O repo.tar.gz \"https://api.github.com/repos/$REPO/tarball/$BRANCH\"\ntar xzf repo.tar.gz --strip-components=1\nrm repo.tar.gz\n\n# Copy helm chart and version so they survive as artifacts\ncp -r $CTX/helm /output/helm\necho $VERSION > /output/version.txt\n\n# Build with Kaniko (Docker tar format, imported via nerdctl on deploy)\n/kaniko/executor --context=$CTX --dockerfile=$CTX/Dockerfile --no-push --tar-path=/output/image.tar --destination=olve-pipelines:$VERSION\n\necho \"Build complete: olve-pipelines:$VERSION\"",
  "environmentVariables": {}
}'
```

## Step 6a: Configure beta processing step

Runs first. Imports the image into k3s containerd (shared between apps-beta and apps) and helm-upgrades `apps-beta` using `helm/values-beta.yaml`. A failing beta deploy blocks prod.

Key points:
- Uses `ls -d /input/*/` to find the production step output (paths use step GUIDs, not names)
- Uses `nerdctl` with k3s containerd socket (`/run/k3s/containerd/containerd.sock`) — NOT `ctr import` (which targets the wrong containerd)
- Cleans `/tmp/olve-pipelines-helm-beta` before scp to prevent directory nesting
- Passes `-f /tmp/olve-pipelines-helm-beta/values-beta.yaml` to apply beta overrides
- `slo.enabled=false` because the sloth CRD is not installed cluster-wide
- Runs a post-install readiness check against `pipelines-beta.ovea.pro/api/health` so the step fails fast if the beta rollout is unhealthy, gating prod

```bash
curl -sk -X PUT "https://pipelines-private.ovea.pro/api/processing-steps/$BETA_ID/configuration" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{
  "image": "alpine:latest",
  "script": "set -e\napk add --no-cache openssh-client curl\n\n# Write SSH key\nmkdir -p ~/.ssh\necho \"$SSH_PRIVATE_KEY\" > ~/.ssh/id_ed25519\nchmod 600 ~/.ssh/id_ed25519\nssh-keyscan -H bulwark-m2 >> ~/.ssh/known_hosts 2>/dev/null || true\n\nINPUT_DIR=$(ls -d /input/*/)\nVERSION=$(cat ${INPUT_DIR}version.txt)\nHOST=oliver@bulwark-m2\n\necho \"Deploying olve-pipelines:$VERSION to apps-beta\"\n\n# Import image into k3s containerd\necho \"Importing image...\"\ncat ${INPUT_DIR}image.tar | ssh -o StrictHostKeyChecking=no $HOST \"sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load\"\n\n# Copy helm chart (clean destination to avoid scp nesting)\necho \"Copying helm chart...\"\nssh -o StrictHostKeyChecking=no $HOST \"rm -rf /tmp/olve-pipelines-helm-beta\"\nscp -o StrictHostKeyChecking=no -r ${INPUT_DIR}helm $HOST:/tmp/olve-pipelines-helm-beta\n\n# Helm upgrade with beta values\necho \"Running helm upgrade (beta)...\"\nssh -o StrictHostKeyChecking=no $HOST \"helm upgrade --install olve-pipelines /tmp/olve-pipelines-helm-beta -n apps-beta -f /tmp/olve-pipelines-helm-beta/values-beta.yaml --set image.repository=docker.io/library/olve-pipelines --set image.tag=$VERSION --set image.pullPolicy=Never --set slo.enabled=false && rm -rf /tmp/olve-pipelines-helm-beta\"\n\n# Wait for rollout and verify reachability — if beta fails, prod must not deploy\necho \"Waiting for beta rollout...\"\nssh -o StrictHostKeyChecking=no $HOST \"kubectl -n apps-beta rollout status deploy/olve-pipelines --timeout=120s\"\n\necho \"Verifying beta /api/health...\"\nfor i in 1 2 3 4 5; do\n  if curl -skf -o /dev/null https://pipelines-beta.ovea.pro/api/health; then\n    echo \"Beta health OK\"\n    exit 0\n  fi\n  sleep 5\ndone\necho \"Beta health check failed\" >&2\nexit 1",
  "environmentVariables": {}
}'
```

## Step 6b: Configure prod processing step

Runs only after beta succeeds.

Key points about this script:
- Uses `ls -d /input/*/` to find the production step output (paths use step GUIDs, not names)
- Uses `nerdctl` with k3s containerd socket (`/run/k3s/containerd/containerd.sock`) — NOT `ctr import` (which targets the wrong containerd)
- Cleans `/tmp/olve-pipelines-helm` before scp to prevent directory nesting
- SSHes to bulwark-m2 to import image and helm upgrade

```bash
curl -sk -X PUT "https://pipelines-private.ovea.pro/api/processing-steps/$PROC_ID/configuration" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{
  "image": "alpine:latest",
  "script": "set -e\napk add --no-cache openssh-client\n\n# Write SSH key\nmkdir -p ~/.ssh\necho \"$SSH_PRIVATE_KEY\" > ~/.ssh/id_ed25519\nchmod 600 ~/.ssh/id_ed25519\nssh-keyscan -H bulwark-m2 >> ~/.ssh/known_hosts 2>/dev/null || true\n\n# Find the production step output (paths use step GUIDs, not names)\nINPUT_DIR=$(ls -d /input/*/)\nVERSION=$(cat ${INPUT_DIR}version.txt)\nHOST=oliver@bulwark-m2\n\necho \"Deploying olve-pipelines:$VERSION\"\n\n# Import image into k3s containerd (MUST use k3s socket, not default containerd)\necho \"Importing image...\"\ncat ${INPUT_DIR}image.tar | ssh -o StrictHostKeyChecking=no $HOST \"sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load\"\n\n# Verify image is visible to CRI\necho \"Verifying image...\"\nssh -o StrictHostKeyChecking=no $HOST \"sudo crictl images | grep olve-pipelines\"\n\n# Copy helm chart (clean destination first to avoid scp nesting)\necho \"Copying helm chart...\"\nssh -o StrictHostKeyChecking=no $HOST \"rm -rf /tmp/olve-pipelines-helm\"\nscp -o StrictHostKeyChecking=no -r ${INPUT_DIR}helm $HOST:/tmp/olve-pipelines-helm\n\n# Helm upgrade\necho \"Running helm upgrade...\"\nssh -o StrictHostKeyChecking=no $HOST \"helm upgrade --install olve-pipelines /tmp/olve-pipelines-helm -n apps --set image.repository=docker.io/library/olve-pipelines --set image.tag=$VERSION --set image.pullPolicy=Never --set slo.enabled=false && rm -rf /tmp/olve-pipelines-helm\"\n\necho \"Deploy complete: olve-pipelines:$VERSION\"",
  "environmentVariables": {}
}'
```

## Step 7: Set pipeline secrets

The secrets (GITHUB_TOKEN, SSH_PRIVATE_KEY) are stored in K8s and survive restarts, but the pipeline ID changes each time. Copy from the most recent previous pipeline's K8s secret:

```bash
# Find the old secret (lists all pipeline secrets in olve-runners namespace)
ssh oliver@bulwark-m2 "kubectl get secrets -n olve-runners | grep olve-pipeline-"

# Extract values from the old secret
OLD_SECRET_NAME="<name from above>"
SECRETS_JSON=$(ssh oliver@bulwark-m2 "kubectl get secret $OLD_SECRET_NAME -n olve-runners -o json | python3 -c \"import sys,json,base64; d=json.load(sys.stdin)['data']; print(json.dumps({k:base64.b64decode(v).decode() for k,v in d.items()}))\"")

# Set each secret on the new pipeline
GITHUB_TOKEN=$(echo "$SECRETS_JSON" | uv run python -c "import sys,json; print(json.load(sys.stdin)['GITHUB_TOKEN'], end='')")
SSH_KEY=$(echo "$SECRETS_JSON" | uv run python -c "import sys,json; print(json.load(sys.stdin)['SSH_PRIVATE_KEY'], end='')")

curl -sk -X PUT "https://pipelines-private.ovea.pro/api/pipelines/$PID/secrets/GITHUB_TOKEN" \
  -H "$H" -H "Content-Type: application/json" \
  -d "{\"value\":\"$GITHUB_TOKEN\"}"

curl -sk -X PUT "https://pipelines-private.ovea.pro/api/pipelines/$PID/secrets/SSH_PRIVATE_KEY" \
  -H "$H" -H "Content-Type: application/json" \
  -d "{\"value\":$(echo "$SSH_KEY" | uv run python -c "import sys,json; print(json.dumps(sys.stdin.read().strip()))")}"
```

## Step 8: Create trigger

### Option A: Poll trigger (recommended — no external webhook needed)

Creates a poll trigger that checks the GitHub API for new commits on `main` every 60 seconds. Requires `GITHUB_TOKEN` to be set as a pipeline secret (Step 7).

```bash
TRIGGER=$(curl -sk -X POST "https://pipelines-private.ovea.pro/api/pipelines/$PID/triggers" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{
    "name":"github-poll",
    "target":{
      "type":"poll",
      "url":"https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/main",
      "headers":{
        "Authorization":"Bearer $SECRET:GITHUB_TOKEN",
        "User-Agent":"Olve.Pipelines",
        "Accept":"application/vnd.github+json"
      },
      "valuePath":"sha",
      "intervalSeconds":60
    }
  }') && \
TRIGGER_ID=$(echo "$TRIGGER" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
echo "Trigger ID: $TRIGGER_ID"
```

### Option B: Webhook trigger

```bash
TRIGGER=$(curl -sk -X POST "https://pipelines-private.ovea.pro/api/pipelines/$PID/triggers" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{"name":"deploy-on-push","target":{"type":"production"}}') && \
TRIGGER_ID=$(echo "$TRIGGER" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
TRIGGER_SECRET=$(echo "$TRIGGER" | uv run python -c "import sys,json; print(json.load(sys.stdin)['secret'], end='')") && \
echo "Trigger ID: $TRIGGER_ID" && \
echo "Trigger secret: $TRIGGER_SECRET"
```

## Step 9: Verify

```bash
# List everything
curl -sk "https://pipelines-private.ovea.pro/api/pipelines"
curl -sk "https://pipelines-private.ovea.pro/api/pipelines/$PID/triggers" -H "$H"
curl -sk "https://pipelines-private.ovea.pro/api/production-steps/$PROD_ID/configuration" -H "$H"
curl -sk "https://pipelines-private.ovea.pro/api/processing-steps/$BETA_ID/configuration" -H "$H"
curl -sk "https://pipelines-private.ovea.pro/api/processing-steps/$PROC_ID/configuration" -H "$H"
```

## After setup

Print the new IDs for the user:
- Pipeline ID
- Production step ID
- Processing step IDs (beta, prod)
- Webhook trigger ID and secret

If a poll trigger was created, tell the user that deployments will happen automatically when new commits are pushed to `main`. They can also run `/deploy` to trigger a manual deployment.

If a webhook trigger was created, tell the user they can run `/deploy` to trigger a deployment.
