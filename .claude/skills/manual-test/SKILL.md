---
name: manual-test
description: Run the app locally and make authenticated API calls against the beta environment for manual testing.
allowed-tools: Bash Read Grep
---

# Manual Testing

## 1. Start the app locally

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/Olve.Pipelines
# Listens on http://localhost:5000
```

**Must** use `Development` environment for local testing — Production mode rejects the beta Authentik cert.

## 2. Get a bearer token

The app requires JWT authentication from Authentik. Use the Storage OIDC credentials (they work for API auth too):

```bash
# Read credentials from user-secrets
dotnet user-secrets list --project src/Olve.Pipelines
# Use Storage:ClientId and Storage:ClientSecret

# Get token (use uv run python, not python3)
TOKEN=$(curl -sk -X POST "https://auth-beta.ovea.pro/application/o/token/" \
  -d "grant_type=client_credentials" \
  -d "client_id=<Storage:ClientId>" \
  -d "client_secret=<Storage:ClientSecret>" \
  -d "scope=openid" | uv run python -c "import sys,json; print(json.load(sys.stdin)['access_token'], end='')")
```

**Important:** Token fetch and API calls must happen in the **same shell invocation** (Bash tool calls don't share env vars). Chain with `&&`.

## 3. API call reference

All mutating endpoints require `Authorization: Bearer $TOKEN`. GET list/get endpoints are anonymous.

### Create pipeline
```bash
curl -s -X POST "http://localhost:5000/api/pipelines?name=my-pipeline" \
  -H "Authorization: Bearer $TOKEN"
# Returns: {"id":"...","name":"my-pipeline"}
```

### Create production step
```bash
curl -s -X POST "http://localhost:5000/api/pipelines/$PIPELINE_ID/production" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"my-step"}'
# Returns: {"id":"...","name":"my-step","pipelineId":"..."}
```

### Set step configuration
```bash
curl -s -X PUT "http://localhost:5000/api/production-steps/$STEP_ID/configuration" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"image":"alpine:latest","script":"echo hello","environmentVariables":null}'
```

### Trigger production
```bash
curl -s -X POST "http://localhost:5000/api/pipelines/$PIPELINE_ID/trigger/production" \
  -H "Authorization: Bearer $TOKEN"
# Returns: job group with artifactBundleId
```

### List jobs
```bash
curl -s http://localhost:5000/api/jobs
# Anonymous. Returns array of jobs with status.
```

### Get job logs
```bash
curl -s http://localhost:5000/api/jobs/$JOB_ID/logs
# Anonymous. Returns log string. Only available after job completes.
```

## 4. Full E2E test (single command)

```bash
TOKEN=$(curl -sk -X POST "https://auth-beta.ovea.pro/application/o/token/" \
  -d "grant_type=client_credentials" \
  -d "client_id=<Storage:ClientId>" \
  -d "client_secret=<Storage:ClientSecret>" \
  -d "scope=openid" | uv run python -c "import sys,json; print(json.load(sys.stdin)['access_token'], end='')") && \
H="Authorization: Bearer $TOKEN" && \
PIPELINE=$(curl -s -X POST "http://localhost:5000/api/pipelines?name=e2e-test" -H "$H") && \
PID=$(echo "$PIPELINE" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
STEP=$(curl -s -X POST "http://localhost:5000/api/pipelines/$PID/production" \
  -H "$H" -H "Content-Type: application/json" -d '{"name":"echo-step"}') && \
SID=$(echo "$STEP" | uv run python -c "import sys,json; print(json.load(sys.stdin)['id'], end='')") && \
curl -s -X PUT "http://localhost:5000/api/production-steps/$SID/configuration" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{"image":"alpine:latest","script":"echo hello from pipeline","environmentVariables":null}' > /dev/null && \
curl -s -X POST "http://localhost:5000/api/pipelines/$PID/trigger/production" -H "$H" && \
echo "" && echo "Triggered. Wait ~10s then check: curl -s http://localhost:5000/api/jobs"
```

## 5. Monitor K8s jobs

Jobs execute on the remote K8s cluster via SSH:

```bash
ssh oliver@bulwark-m2 "kubectl get jobs,pods -n olve-runners-beta"
ssh oliver@bulwark-m2 "kubectl logs <pod-name> -n olve-runners-beta"
```

## Notes

- The app connects to MinIO at `https://minio-beta.ovea.pro` and K8s via OpenBao at `https://openbao-beta.ovea.pro`
- Tokens expire after 1 hour — re-run the token command if you get 401s
- The `olve-runners-beta` namespace is where K8s jobs are created
- Pipeline creation takes `name` as a **query parameter**, not JSON body
- Production/processing step creation takes `name` in a **JSON body**
