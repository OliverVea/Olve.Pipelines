---
name: manual-test
description: Run the app locally or target v2 beta (or v2 prod), and make authenticated API calls (via pl or curl) for manual testing of the v2 run/version/block API.
allowed-tools: Bash Read Grep
---

# Manual Testing (v2)

## Environments

| Environment | Base URL | Auth provider | Runner namespace |
|---|---|---|---|
| Local | `http://localhost:5000` | `Auth:Disable=true`, or the compose stack's Dex | as configured |
| beta | `https://pipelines-beta.ovea.pro` | its Dex, `/dex` (GitHub) | `olve-runners-beta` |
| prod | `https://pipelines-private.ovea.pro` | its Dex, `/dex` (GitHub) | `olve-runners` |

v2 beta is the
place to experiment; v2 prod hosts the self-deploy pipeline (see the end of section 3).

## 1. Start the app locally (optional)

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/backend/Olve.Pipelines        # http://localhost:5000
```

Local config comes from
`dotnet user-secrets` (`dotnet user-secrets list --project src/backend/Olve.Pipelines`); with
`Auth:Disable=true` no token is needed. Without Kubernetes/storage config, runs can't execute.

## 2. Authenticate

**Use `pl`** with a profile per instance (the user logs in; `--device` over SSH):

```bash
pl login --device --profile beta --api-url https://pipelines-beta.ovea.pro
pl pipeline list --profile beta
```

For raw `curl`, reuse the token `pl` cached (any `pl` command refreshes it):

```bash
pl pipeline list --profile beta >/dev/null && TOKEN=$(jq -r .profiles.beta.auth.accessToken ~/.pl)
```

Only users log in (Dex, GitHub, the allow-list); there is no client-credentials token. Scripts use
the token of a `pl` login, which `pl` accepts back as `PIPELINES_API_TOKEN`.

## 3. API call reference

Every call needs `Authorization: Bearer $TOKEN`. Errors are `400` with a `ResultProblem[]` body. Most of these have a
`pl` equivalent (`pl help`) — prefer it.

```bash
BASE=https://pipelines-beta.ovea.pro
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
Don't experiment on "Olve.Pipelines v2" on v2 prod — its runs redeploy v2 beta and v2 prod.

## 4. Kubernetes

```bash
ssh oliver@bulwark-m2 "kubectl -n olve-runners-beta get jobs,pods -l olve-pipeline=<id without dashes>"
ssh oliver@bulwark-m2 "kubectl -n olve-runners-beta logs <pod> -c step"        # init container: -c olve-init
ssh oliver@bulwark-m2 "kubectl -n apps-beta logs deploy/olve-pipelines-v2 --since=10m | grep -v health"
```

Job names: step attempts `olve-…` with labels `olve-role=step`, `olve-run`, `olve-step`,
`olve-attempt`; build Jobs `olve-build-<p12>-<sha12>`; failure handlers `olve-fh-…`. Prod: the same
commands with `-n olve-runners` / `-n apps`.
