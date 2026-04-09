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

For **production** (deployed instance):
```bash
TOKEN=$(curl -sk -X POST "https://auth.ovea.pro/application/o/token/" \
  -d "grant_type=client_credentials" \
  -d "client_id=olve-pipelines" \
  -d "client_secret=d178464f2442ec91434117c488e1f70706ed03458634c4cace376d998bc59020" \
  -d "scope=openid" | uv run python -c "import sys,json; print(json.load(sys.stdin)['access_token'], end='')")
```

**Important:** Token fetch and API calls must happen in the **same shell invocation** (Bash tool calls don't share env vars). Chain with `&&`.

## 3. API base URLs

| Environment | Base URL | Auth provider |
|---|---|---|
| Local | `http://localhost:5000` | `auth-beta.ovea.pro` |
| Production | `https://pipelines-private.ovea.pro` (use `-sk` with curl) | `auth.ovea.pro` |

## 4. API call reference

All mutating endpoints require `Authorization: Bearer $TOKEN`. GET list/get endpoints are anonymous.

### Create pipeline
```bash
curl -s -X POST "$BASE/api/pipelines?name=my-pipeline" \
  -H "Authorization: Bearer $TOKEN"
# Returns: {"id":"...","name":"my-pipeline"}
```

### Create production step
```bash
curl -s -X POST "$BASE/api/pipelines/$PIPELINE_ID/production" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"my-step"}'
```

### Create processing step
```bash
curl -s -X POST "$BASE/api/pipelines/$PIPELINE_ID/processing" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"my-step"}'
```

### Set step configuration
```bash
curl -s -X PUT "$BASE/api/production-steps/$STEP_ID/configuration" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"image":"alpine:latest","script":"echo hello","environmentVariables":{}}'
# Same pattern for processing-steps
```

### Set pipeline secret (one at a time)
```bash
curl -s -X PUT "$BASE/api/pipelines/$PIPELINE_ID/secrets/MY_SECRET" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"value":"secret-value-here"}'
```

### Create webhook trigger
```bash
curl -s -X POST "$BASE/api/pipelines/$PIPELINE_ID/triggers" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"my-trigger","target":{"$type":"production"}}'
# Returns: {"id":"...","secret":"..."}
```

### Create poll trigger
```bash
curl -s -X POST "$BASE/api/pipelines/$PIPELINE_ID/triggers" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{
    "name":"my-poll",
    "target":{
      "$type":"poll",
      "url":"https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/main",
      "headers":{
        "Authorization":"Bearer $SECRET:GITHUB_TOKEN",
        "User-Agent":"Olve.Pipelines",
        "Accept":"application/vnd.github+json"
      },
      "valuePath":"sha",
      "intervalSeconds":60
    }
  }'
# Returns: {"id":"...","secret":"..."}
# The poller starts automatically — no webhook call needed
# Header values with $SECRET:NAME are resolved from pipeline K8s secrets at poll time
```

### Fire webhook trigger
```bash
curl -s -X POST "$BASE/api/webhooks/$TRIGGER_ID" \
  -H "Authorization: Bearer $TRIGGER_SECRET"
# No app-level auth — the trigger secret IS the auth
```

### Trigger production (direct, needs app auth)
```bash
curl -s -X POST "$BASE/api/pipelines/$PIPELINE_ID/trigger/production" \
  -H "Authorization: Bearer $TOKEN"
```

### List jobs
```bash
curl -s $BASE/api/jobs
# Anonymous. Returns array of jobs with status.
```

### Get job logs
```bash
curl -s $BASE/api/jobs/$JOB_ID/logs
# Anonymous. Returns log string. Only available after job completes.
```

## 5. Monitor K8s jobs

Jobs execute on the remote K8s cluster via SSH:

```bash
# Production runners
ssh oliver@bulwark-m2 "kubectl get jobs,pods -n olve-runners"
# Beta runners
ssh oliver@bulwark-m2 "kubectl get jobs,pods -n olve-runners-beta"
# Pod logs
ssh oliver@bulwark-m2 "kubectl logs <pod-name> -n olve-runners -c runner"
```

## Notes

- **Beta**: MinIO at `minio-beta.ovea.pro`, K8s via OpenBao at `openbao-beta.ovea.pro`, runners in `olve-runners-beta`
- **Production**: MinIO at `minio.ovea.pro`, K8s via OpenBao at `openbao.ovea.pro`, runners in `olve-runners`
- Tokens expire after 1 hour — re-run the token command if you get 401s
- Pipeline creation takes `name` as a **query parameter**, not JSON body
- Production/processing step creation takes `name` in a **JSON body**
- Secrets endpoint is `PUT .../secrets/{name}` with `{"value":"..."}` — one secret at a time, not bulk
