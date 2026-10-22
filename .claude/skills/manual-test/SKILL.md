---
name: manual-test
description: Run the app locally or target v2 beta, and make authenticated API calls (via pl or curl) for manual testing of the v2 run/version/gate API.
allowed-tools: Bash Read Grep
---

# Manual Testing (v2)

## Environments

| Environment | Base URL | Auth provider | Runner namespace |
|---|---|---|---|
| Local | `http://localhost:5000` | `auth-beta.ovea.pro` (or `Auth:Disable=true`) | as configured |
| v2 beta | `https://pipelines-v2-beta.ovea.pro` | `auth-beta.ovea.pro` (shared with v1 beta) | `olve-runners-beta` |
| v1 prod (main) | `https://pipelines-private.ovea.pro` | `auth.ovea.pro` | `olve-runners` |

v1 instances run v1 code from `main`; the v2 API below exists only on v2 instances.

## 1. Start the app locally (optional)

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/Olve.Pipelines        # http://localhost:5000
```

Use `Development` — Production mode rejects the beta Authentik cert. Local config comes from
`dotnet user-secrets` (`dotnet user-secrets list --project src/Olve.Pipelines`); with
`Auth:Disable=true` no token is needed. Without Kubernetes/storage config, runs can't execute.

## 2. Authenticate

**Preferred: `pl`.** Use a v2 `pl` (`dotnet publish src/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/opt/pl-v2`)
with its own config file so the v1 session in `~/.pl` survives:

```bash
export PATH=~/.local/opt/pl-v2:$PATH PIPELINES_CONFIG=~/.pl-v2 PIPELINES_API_URL=https://pipelines-v2-beta.ovea.pro
pl login                                    # --device over SSH
```

For raw `curl`, reuse the token `pl` cached (it is refreshed by any `pl` command):

```bash
pl pipeline list >/dev/null && TOKEN=$(jq -r .auth.accessToken ~/.pl-v2)
```

**Client credentials** (scripts, no browser): the client id and secret are in the Kubernetes Secret
`olve-pipelines-oidc` (keys `client-id`, `client-secret`) — namespace `apps-beta` for beta/v2 beta,
`apps` for prod. Read them from the cluster at the moment you need them; **never paste a client
secret into this file, a doc, a commit or a log.**

```bash
TOKEN=$(curl -sk -X POST "https://auth-beta.ovea.pro/application/o/token/" \
  -d grant_type=client_credentials -d scope=openid \
  --data-urlencode "client_id=$CLIENT_ID" --data-urlencode "client_secret=$CLIENT_SECRET" | jq -r .access_token)
```

Token fetch and API calls must run in the **same shell invocation** (Bash tool calls don't share
env vars) — chain with `&&`. Tokens expire after ~1 hour.

## 3. API call reference

Reads (`GET`) are anonymous except secret listing and step-file listing/downloads. Mutations need
`Authorization: Bearer $TOKEN`. Errors are `400` with a `ResultProblem[]` body. Most of these have a
`pl` equivalent (`pl help`) — prefer it.

```bash
BASE=https://pipelines-v2-beta.ovea.pro
P=<pipelineId>          # pl pipeline list --json | jq -r '.[] | "\(.id) \(.name)"'

# pipelines, versions, health
curl -s $BASE/api/pipelines/summary
curl -s $BASE/api/pipelines/$P/health
curl -s $BASE/api/pipelines/$P/versions
curl -s $BASE/api/pipelines/$P/versions/3 | jq -r .manifest

# start a run: the body is the pipeline input (validated; defaults filled)
curl -s -X POST $BASE/api/pipelines/$P/runs -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"sha":"<40 hex>","mode":"normal"}'

# runs and steps
curl -s "$BASE/api/pipelines/$P/runs?pageSize=5"
curl -s $BASE/api/pipelines/$P/runs/12
curl -s $BASE/api/pipelines/$P/runs/12/steps/hello/output
curl -s "$BASE/api/pipelines/$P/runs/12/steps/hello/logs?attempt=1"
curl -s $BASE/api/pipelines/$P/runs/12/steps/hello/files -H "Authorization: Bearer $TOKEN"

# operate
curl -s -X POST $BASE/api/pipelines/$P/runs/12/cancel -H "Authorization: Bearer $TOKEN"
curl -s -X POST $BASE/api/pipelines/$P/runs/12/rerun -H "Authorization: Bearer $TOKEN"
curl -s -X POST $BASE/api/pipelines/$P/runs/12/continue -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"input":{"mode":"normal"}}'
curl -s -X POST $BASE/api/pipelines/$P/runs/12/steps/count/rerun -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"continue":true}'
curl -s -X POST $BASE/api/pipelines/$P/steps/last/block -H "Authorization: Bearer $TOKEN"
curl -s -X POST $BASE/api/pipelines/$P/steps/last/unblock -H "Authorization: Bearer $TOKEN"
curl -s -X POST $BASE/api/pipelines/$P/switch -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"version":3,"cancelRunning":false}'

# bindings and reconcile
curl -s $BASE/api/pipelines/$P/binding/status
curl -s -X POST $BASE/api/pipelines/$P/binding/reconcile -H "Authorization: Bearer $TOKEN"

# validate a manifest without pushing (what `pl dev validate` does)
pl dev describe --dir .pipelines-test --json > /tmp/manifest.json
curl -s -X POST $BASE/api/manifests/validate -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' --data-binary @/tmp/manifest.json
```

Secrets: `pl secret set <pipelineId> <NAME>` (stdin, verbatim — don't use `$(…)`, it strips the
trailing newline of SSH keys).

The v2 fixture pipeline (`.pipelines-test/`, "v2 test fixture" on v2 beta) is the safe target for
experiments: input `{ sha, mode: normal | skip-last | fail }` exercises `when` skips and `onFailure`.
Don't experiment on "Olve.Pipelines v2" — its runs redeploy v2 beta.

## 4. Kubernetes

```bash
ssh oliver@bulwark-m2 "kubectl -n olve-runners-beta get jobs,pods -l olve-pipeline=<id without dashes>"
ssh oliver@bulwark-m2 "kubectl -n olve-runners-beta logs <pod> -c step"        # init container: -c olve-init
ssh oliver@bulwark-m2 "kubectl -n apps-beta logs deploy/olve-pipelines-v2 --since=10m | grep -v health"
```

Job names: step attempts `olve-…` with labels `olve-role=step`, `olve-run`, `olve-step`,
`olve-attempt`; build Jobs `olve-build-<p12>-<sha12>`; failure handlers `olve-fh-…`. The runner
namespace is shared with v1 beta.
