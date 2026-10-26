# Running an Instance

[← Index](index.md)

An instance is the server (one container), its own Garage (S3-compatible storage) and a Kubernetes
namespace where it runs build and step Jobs. The Helm chart in `helm/` installs all of it
(`helm/values-minimal.yaml` is the smallest profile). An instance can also deploy itself: the
repo's own pipeline (`.pipelines/`) does, configured per instance ([Pipeline Config](configuration.md)).

## Installing on a cluster

Released images are public: the server at `ghcr.io/olivervea/olve-pipelines` (`latest`, `next`
and one tag per release, e.g. `2026.10.24.59`) and the step pods' init image at
`ghcr.io/olivervea/olve-pipelines-harness-base`. The chart defaults to the server's `latest`.

`pl bootstrap` (`pl` from the latest [release](cli.md#installing)), run from a checkout of the repo, installs the chart's minimal profile: the server
and its Garage, with no ingress and auth off. It is idempotent, and `pl teardown` removes it (the
Garage data is kept unless `--purge-data`).

```bash
pl bootstrap -n <namespace> --chart helm                 # --image-tag <release> pins a version
```

To run your own build instead, `docker build -t <registry>/olve-pipelines:<tag> .`, push it, and
pass `--image-repository <registry>/olve-pipelines --image-tag <tag>`.

For anything beyond trying it, set `auth` (the chart refuses to render without it; `mode: dex` runs
the [bundled Dex](#bundled-dex)) and whatever else differs through `config.*` (the
[server configuration](#server-configuration) below). The chart's defaults assume nothing about the
cluster: the bundled Garage on the default StorageClass, step Jobs in the release namespace, plain
runc, no Ingress, no webhook registration (`config.Webhooks__PublicBaseUrl` turns it on). A local
instance needs no cluster at all: [Docker Compose](#a-local-instance-with-docker-compose).

## Server configuration

Sources in priority order (highest wins): CLI args (`--Port 9090`), user secrets
(`dotnet user-secrets set "Key" "value"`), environment variables (`Storage__Bucket`),
`appsettings.json`.

| Key | Default | Description |
|-----|---------|-------------|
| `Host` / `Port` | `localhost` / `5000` | Listen address |
| `Auth:Authority` | — | OIDC authority |
| `Auth:Audience` | `olve-pipelines` | JWT audience |
| `Auth:SigningKey` | — | Local HS256 key (bypasses OIDC, for dev) |
| `Auth:Disable` | `false` | Disable auth (local dev, minimal install) |
| `Auth:FrontendAuthority` / `Auth:FrontendAudience` | — | OIDC authority and client id served to the UI and `pl login` |
| `Auth:RequireHttpsMetadata` | `true` | `false` allows a plain-HTTP OIDC provider (the local compose stack's Dex) |
| `Auth:Allow:Subjects` / `Users` / `Orgs` / `Emails` | — | Who may use the API: subject ids (`sub`, exact), logins (`preferred_username`), orgs or `org:team` (`groups`), verified emails. A valid token outside the lists gets 403 and logs a warning with its login and subject, ready to copy into `Subjects`; prefer subjects to logins, which can be renamed and taken by someone else. With all four empty any valid token gets in, so set them when the provider lets anyone sign in (Dex with GitHub) |
| `Storage:Mode` | `Persistent` | `Ephemeral` = in-memory only |
| `Storage:Endpoint` | — | The instance's Garage (S3-compatible) endpoint |
| `Storage:AccessKey` / `Storage:SecretKey` | — | S3 credentials |
| `Storage:Bucket` | `olve-pipelines` | Bucket |
| `Storage:InClusterEndpoint` | `Storage:Endpoint` | Garage endpoint as step pods reach it; pre-signed URLs are signed for this host (`Host` is in the SigV4 signature) |
| `Storage:Region` | `us-east-1` | SigV4 region for pre-signed URLs (Garage's `s3_region`) |
| `Storage:PresignExpirySeconds` | `3600` | Default pre-signed URL expiry (step URLs get the step deadline + grace) |
| `Kubernetes:Namespace` | — (the chart: the release namespace) | Runner namespace for step Jobs and pipeline Secrets |
| `Kubernetes:AuthMode` | — | `InCluster`: the pod's ServiceAccount reaches the cluster API (the chart sets it and grants a Role in the runner namespace). Unset = no Kubernetes |
| `Kubernetes:RuntimeClassName` | — | RuntimeClass for step pods (`gvisor`); empty = runc |
| `Kubernetes:StepServiceAccount` | — | ServiceAccount (in the runner namespace) whose token steps that declare `clusterAccess` get (`steps.helmDeploy` in-cluster); empty = no step pod gets a token |
| `Harness:HarnessBaseImage` | `ghcr.io/olivervea/olve-pipelines-harness-base:src-…` (`HarnessImage.cs`) | Step-pod init image (pinned, never `latest`). The chart sets it from `harness.image`, which the deploy fills from the `publish-harness` step |
| `Harness:BunImage` | `oven/bun:1.4.3-slim` | Build/describe Job image (glibc, never alpine) |
| `GitHub:ApiUrl` / `GitHub:CodeloadUrl` | `https://api.github.com` / `https://codeload.github.com` | Where bindings read config cursors and the build Job fetches tarballs (GitHub Enterprise, or a local stand-in for tests) |
| `Harness:DefaultStepTimeoutSeconds` / `MaxStepTimeoutSeconds` / `StartupGraceSeconds` | `3600` / `21600` / `120` | Step Job `activeDeadlineSeconds` = min(timeout, max) + grace |
| `Harness:UrlExpiryGraceSeconds` | `600` | Added to the deadline for step pre-signed URLs |
| `Harness:ScratchSizeLimit` | `1Gi` | `sizeLimit` of the step pod's `/olve` emptyDir |
| `Harness:StepCpuRequest` / `StepMemoryRequest` / `StepMemoryLimit` | — | Optional step container resources |
| `Harness:BuildTimeoutSeconds` / `BuildScratchSizeLimit` | `900` / `4Gi` | Build/describe Job deadline and scratch size |
| `Harness:BuildCpuRequest` / `BuildMemoryRequest` / `BuildMemoryLimit` | `500m` / `512Mi` / `3Gi` | Build/describe Job resources |
| `Harness:BuildTokenSecretKey` | `GITHUB_TOKEN` | Pipeline-secret key used for the repo tarball when the binding names no credentials key |
| `Jobs:MaxConcurrent` | `4` | Step Jobs running at once across all pipelines; the rest wait in Scheduled |
| `Reconcile:PollIntervalSeconds` | `900` | Deploy poll interval per binding |
| `Webhooks:PublicBaseUrl` | — | Public origin GitHub posts push webhooks to; empty = no webhook registration (the deploy poll still notices pushes). Changing it repoints the registered hooks |
| `Links:BaseUrl` | `Webhooks:PublicBaseUrl` | Base of the run/log links in a failure handler's `FailureContext` |
| `Retention:Enabled` / `Retention:DryRun` | `true` / `true` | Retention sweeps; **dry run by default** (logs only) — set `Retention__DryRun=false` to delete |
| `Retention:KeepLatestRuns` / `Retention:MinRunAgeDays` | `20` / `183` | A run is deleted only when beyond the latest N **and** older than this |
| `Retention:BuildGraceDays` | `7` | Unused compiled binaries are deleted after this |
| `Retention:OrphanGraceDays` / `ResultArchiveGraceHours` | `7` / `24` | Leftover objects of deleted runs / stale `result.tar.gz` |
| `Retention:ClusterGraceMinutes` / `FinishedJobMaxAgeDays` | `60` / `7` | URL Secrets and leaked step Jobs / TTL-less finished Jobs |
| `Retention:IntervalHours` / `StartupDelayMinutes` | `24` / `2` | Sweep schedule |
| `Distribution:CliPipeline` / `Distribution:CliStep` | — / `publish-cli` | Pipeline (name or id) whose latest successful `CliStep` attempt `/download` serves; unset = nothing served. Retention keeps that run |
| `Distribution:CliPromotedBy` | — | Only runs in which this step (e.g. `promote`) also succeeded count for `/download` |
| `Distribution:LocalDirectory` | — | Serve `/download` assets from this directory when it has them (the compose stack builds `pl` from source) |
| `Deployment:Image` | — | The image this instance runs, reported by `/api/health`. The compose deployer sets it (`Deployment__Image`) when it recreates the container |
| `OpenTelemetry:Endpoint` | — | OTLP endpoint (traces, metrics and logs under `/v1/…`); unset or empty = no exporter |
| `OpenTelemetry:Protocol` | gRPC | `http` for OTLP over HTTP/protobuf |
| `OpenTelemetry:Headers` | — | OTLP headers, `k1=v1,k2=v2` (the `OTEL_EXPORTER_OTLP_HEADERS` format), e.g. a backend's API key. The chart reads it from a Secret (`telemetry.headers.kubernetesSecret`) |
| `Logging:Console:FormatterName` | `simple` | Console formatter. `json` in deployed envs (see below) |
| `Logging:Console:Json:UseUtcTimestamp` | `false` | UTC timestamps with the `json` formatter |

### Bundled Dex

The chart can run [Dex](https://dexidp.io) as the instance's OIDC provider, with GitHub as the
upstream (`dex.enabled: true`, `dex.issuer: https://<host>/dex`). It stores its keys and refresh
tokens in sqlite on a small PVC that survives `helm uninstall`. Route `<host>/dex` to the
`<release>-dex` Service (port 5556); the rest of the host stays on the server.

1. Create a GitHub OAuth app with the callback `https://<host>/dex/callback`.
2. Store its credentials as the Secret `olve-pipelines-dex` (keys `github-client-id`,
   `github-client-secret`) in the release namespace.
3. Point the server at it: `auth.mode: dex`, `issuer: https://<host>/dex`, `clientId: olve-pipelines`,
   and an `allow` list. GitHub lets anyone sign in to Dex, so the chart refuses `dex` without one.

Dex has one public client, `olve-pipelines`, for the web UI and `pl`. It accepts only the UI's
`https://<host>/callback` and the device flow, so sign in with `pl login --device`.

## Logging

Logging is **backend-agnostic**: the app only writes to **stdout**. Shipping is the cluster's
job (for example an OTel Collector DaemonSet scraping container stdout). The app names no log
endpoint, token, or stream.

- **Format.** Deployed environments set `Logging__Console__FormatterName=json` (in `helm/values.yaml`,
  inherited by beta) so every line is structured JSON and message args (`{RunNumber}`, `{StepName}`, …)
  become individually queryable fields. Local `dotnet run` gets the pretty `simple` formatter. No
  application code selects the formatter — it is pure configuration.
- **Requests.** Every `/api` request logs one line, `{Method} {Path} {StatusCode} {Elapsed}ms by {User}`
  (the token's `preferred_username`, else its subject, else `anonymous`), so a cancel or switch
  can be traced to who sent it.
- **Levels.** `Information` = operator-facing audit milestones (run started, attempt finished, trigger
  fired, reconcile done); `Warning` = recoverable/degraded + **always-logged security events** (webhook
  signature mismatch, auth rejection); `Error` = an operation failed / unhandled background exception;
  `Debug`/`Trace` = loop iterations and wire detail, off in prod.

## A local instance with Docker Compose

`docker-compose.yml` runs the full application as a throwaway instance, for example for agent evals.
Everything is built from this checkout, and nothing talks to the homelab. It runs:

- **The server image**, built from the root `Dockerfile`. Dashboard, API and `/docs` are at
  <http://localhost:5080>.
- **Garage** for storage, as in the chart.
- **A single-node k3s** in a privileged container. It runs the build Jobs and the step Jobs.
- **The step pods' init image.** `harness-build` builds it from `harness/base` with kaniko and
  pushes it to a password-protected local `registry:2`. k3s pulls it from there over plain HTTP.
- **`pl`.** `pl-build` builds it from source, and the server serves it at `/download`, so
  `curl -fsSL http://localhost:5080/install.sh | sh` works. The server doesn't wait for it: until the
  build lands (several minutes of AOT compiling), `/download` answers 503 and `install.sh` exits
  with "pl is still being built".
- **Dex** for login, at <http://localhost:5556/dex>. The test user is `dev@example.com` with
  password `password`.
- **The compose deployer** (`compose/deployer`), the only service with the Docker socket. The
  self-deploy pipeline asks it to recreate the `pipelines` service with a new image
  ([Deploying to Docker Compose](compose-deploy.md)).

```bash
docker compose up -d --build     # first run builds everything: several minutes
docker compose down -v           # stop and wipe all state (the reset between eval runs)
```

- **Auth is on by default and uses Dex.** `pl login` and the dashboard both log in through it.
  `pl login` uses the device flow when there is no local browser, as in WSL or a container.
  - To run without auth, set `PIPELINES_AUTH_DISABLE=true docker compose up -d`. Every endpoint is
    then anonymous.
  - A script can get a token without a browser through Dex's password grant:

    ```bash
    curl -s localhost:5556/dex/token -d grant_type=password -d client_id=olve-pipelines \
      -d username=dev@example.com -d password=password -d 'scope=openid profile email' | jq -r .access_token
    ```

    Pass the result as `PIPELINES_API_TOKEN`.
- **Pointing `pl` at the instance.** Give it its own profile, and keep `~/.pl` out of it with
  `PIPELINES_CONFIG`:

  ```bash
  export PIPELINES_CONFIG=/tmp/pl-local.json PIPELINES_PROFILE=local
  pl login --api-url http://localhost:5080        # Dex; device flow on a headless box
  pl binding create OliverVea/Olve.Pipelines --branch main --path .pipelines-test   # the v2 fixture
  pl binding reconcile <pipelineId>
  pl wait reconcile <pipelineId> <sha touching .pipelines-test or sdk>
  pl trigger <pipelineId> --field sha=<sha>
  pl wait run '<pipelineId>#1'
  ```

- **Requirements.**
  - Docker must allow privileged containers.
  - Ports 5080 and 5556 must be free. Set `PIPELINES_LOCAL_PORT` to move 5080.
  - Step pods need internet access for image pulls, GitHub tarballs and npm.
  - Bind a public repo, or set a token with `pl binding set-credentials`. Unauthenticated GitHub API
    calls are limited to 60 an hour.
  - Webhooks are never registered because `Webhooks:PublicBaseUrl` is unset. The deploy poll still
    works.
- **Rebuilding the harness image.** It is tagged `:local`, and k3s pulls it only if it isn't
  present yet. After changing `harness/base`, run `docker compose down -v` (or delete the
  `k3s-data` volume).
- **Windows checkouts.** `.gitattributes` keeps every file that ends up in a container LF. A
  checkout made before it existed still has CRLF files, so re-check them out once:

  ```bash
  git rm -r --cached -q . && git reset --hard   # discards uncommitted changes
  ```

- **Proving there is no homelab dependency.** `compose/isolation.yml` maps `registry.ovea.pro`,
  `auth.ovea.pro`, `otel.ovea.pro`, `hub-cache.ovea.pro` and `mcr-cache.ovea.pro` to 127.0.0.1 for
  the server, Dex and the k3s node:

  ```bash
  docker compose -f docker-compose.yml -f compose/isolation.yml up -d --build
  ```

- **Inspecting the cluster:** `docker compose exec k3s kubectl -n olve-runners get jobs,pods`.

### Watch it deploy itself

The stack can run Olve.Pipelines' own deploy pipeline (`.pipelines/`), the one that deploys prod,
against itself: one instance that redeploys itself, with no beta. Only the config differs from prod
([compose/selfdeploy.config.yaml](https://github.com/OliverVea/Olve.Pipelines/blob/main/compose/selfdeploy.config.yaml)):

- `target.beta` is `kind: none`, so the beta deploy and `beta-test` are left out.
- `target.prod` is `kind: compose`, so the pipeline's `select` picks `composeDeploy`: `deploy-prod`
  asks the `deployer` service to recreate the `pipelines` service (the instance running the
  pipeline) with the image the run built. The deployer waits for `/api/ready` and puts the old
  container back if the new one doesn't come up.
- Images go to the compose registry (`registry:5000`); `next` and `latest` are tags moved there.
  npm publishing is off, so `publish-sdk` is skipped and `promote` moves no dist-tag. No step talks
  to npm's registry for publishing, or to ghcr.io.
- `auth: none` and `telemetry: none` for the deployed instance.

```bash
export PIPELINES_CONFIG=/tmp/pl-local.json PIPELINES_PROFILE=local   # and log in, as above
pl binding create OliverVea/Olve.Pipelines --branch <a branch you pushed> --path .pipelines
pl binding status "Olve.Pipelines v2"        # reconciled; pending until it has config
printf local-registry | pl secret set "Olve.Pipelines v2" REGISTRY_PASSWORD
printf local-deployer | pl secret set "Olve.Pipelines v2" DEPLOYER_TOKEN
pl config set "Olve.Pipelines v2" -f compose/selfdeploy.config.yaml   # activates the version
pl trigger "Olve.Pipelines v2" --field sha=<the branch head>           # or push a commit
pl wait run "Olve.Pipelines v2#1" --until finished
curl -s http://localhost:5080/api/health      # image: the digest the run deployed
```

The run builds every component (the server image, `pl`, the SDK; minutes of AOT compiling and
several GB of memory in k3s), publishes the prereleases, deploys the instance and promotes. A first
run on a fresh stack also pulls the build images. The redeployed instance keeps its state: it is the
same container config and the same Garage, only the image changes.

- The deploy poll checks the branch every 15 minutes (`Reconcile:PollIntervalSeconds`), and webhooks
  are off in the stack: after a push, `pl binding reconcile` picks it up at once.
- The run survives the redeploy: the new server reattaches to the run's Jobs, as on prod.
- `docker compose up` names `olve-pipelines-v2:local` again, so it can put the locally built image
  back over a deployed one.

**The in-cluster variant** ([compose/selfdeploy-incluster.config.yaml](https://github.com/OliverVea/Olve.Pipelines/blob/main/compose/selfdeploy-incluster.config.yaml))
tests the helm chart instead: beta and prod are the namespaces `selfdeploy-beta` and
`selfdeploy-prod` of the compose k3s, both `kind: inCluster`, so the pipeline's `select` picks
`helmDeployInCluster`. It runs helm in its own pod as the step ServiceAccount `olve-deployer`
(`Kubernetes__StepServiceAccount`), which the bootstrap makes admin of the two `selfdeploy-*`
namespaces and nothing else (`compose/k3s-selfdeploy.yaml`). Set it with
`pl config set "Olve.Pipelines v2" -f compose/selfdeploy-incluster.config.yaml` (only
`REGISTRY_PASSWORD` is needed). The run also deploys beta and smoke-tests it.

- Each deployed copy has its own Garage and runs its own step Jobs in its namespace, with the
  harness image the run built. On the first install the server restarts a few times until its new
  Garage answers; that is the readiness gate working, not a failure.
- Look at a copy with `docker compose exec k3s kubectl -n selfdeploy-prod get pods`. To drive one,
  run `kubectl -n selfdeploy-beta port-forward --address 0.0.0.0 svc/olve-pipelines 18081:80` inside
  the k3s container, then point `pl` (auth is off) at `http://k3s:18081` from a container on the
  compose network.
