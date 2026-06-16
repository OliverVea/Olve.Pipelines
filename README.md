# Olve.Pipelines

A lightweight CD pipeline configuration and orchestration service for a homelab. Manages pipeline definitions with production steps (parallel builds) and processing steps (sequential deployments), executed as Kubernetes Jobs.

## Pipeline Model

```
Production [N steps, parallel] ──(ArtifactBundle)──> Processing 1 ──> ... ──> Processing N [sequential]
```

- **Production steps** run in parallel. Each produces output to `bundle/<step-name>/`. Combined output = ArtifactBundle.
- **Processing steps** run sequentially. Each receives the full ArtifactBundle.
- Every step is configured as `(image, script, env)` and executed as a Kubernetes Job.

### Promotion gate (state, not config)

A **promotion** is the ArtifactBundle advancing *into* a processing step. Each processing step
has a gate that can **block** promotion (the bundle stops at that step instead of cascading on) and
a **re-promote** action that redrives the step's last-used bundle. This is operational **state, not
GitOps config**: it is API-mutable and stays available even on a git-bound pipeline (configuration
is git-only, but operations like blocking/unblocking and re-promoting are allowed). The blocked set
is persisted separately (`promotion-state.json`) so a braked step stays braked across a restart.
`blocked` is orthogonal to job status — a step can be `Done` *and* have its promotion blocked.

## GitOps Configuration

> **Adding CD to your own repo?** This is the section for you. You don't deploy or modify this
> service — you add one file to *your* repository and bind a pipeline to it.

A pipeline can be **bound to a Git repository**. Once bound, *your* repo is the single source of
truth for the pipeline's shape: a background reconcile loop polls your branch head (~5 min), and
whenever `<your-repo>/.pipelines/config.yaml` changes it materializes the production steps,
processing steps, and triggers to match — then runs the build. Binding also configures the deploy
poll, so pushing a commit deploys automatically; you never author a trigger by hand.

### Adding CD to your repo

1. Add `.pipelines/config.yaml` (+ optional `.pipelines/scripts/*.sh`) to your repository — see
   the schema below. [`Olve.Template.Api`](https://github.com/OliverVea/Olve.Template.Api) ships a
   copy-me [`.pipelines/`](https://github.com/OliverVea/Olve.Template.Api/tree/main/.pipelines)
   starter (Kaniko build + Helm deploy).
2. Create a pipeline bound to your repo: `POST /api/pipelines/with-repo` with
   `{ name, repo, branch?, path?, credentialsSecret }` (see [GitOps Binding](#gitops-binding)).
3. Set the secret *values* your config declares: `PUT /api/pipelines/{id}/secrets/{name}`.
4. Push. The first reconcile builds your pipeline from the file; check
   `GET /api/pipelines/{id}/binding/status` for the result.

**Git-only:** a bound pipeline **rejects API config-mutation endpoints** (step/config/trigger
CRUD) — your repo is the only config writer. Operational endpoints stay open: manual production
trigger, job cancel, and setting secret *values*.

### `config.yaml` schema

`<path>` defaults to `.pipelines`. Steps may be inlined or extracted: `$ref: steps/<name>.yaml`
pulls a step from its own file, and `scriptFile: scripts/<name>.sh` keeps a script out-of-line
(mutually exclusive with `script:`). Secrets are declared by **name only** — values live in the
pipeline's own k8s secret (`olve-pipeline-{id}`), never in the repo — and are referenced as
`$SECRET:NAME` (in env values / poll headers) or as ordinary env vars mounted into every job.

```yaml
apiVersion: "0.0"                 # major must match the server (currently 0.x)
name: my-app
description: Build and deploy.    # optional
version: "1"                      # optional, free-form

secrets:                          # declared by NAME ONLY (values never in repo)
  - name: GITHUB_TOKEN
    description: Read token to fetch the repo tarball.

productionSteps:                  # run in parallel → bundle/<step-name>/
  - name: build
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh        # or inline: script: |

processingSteps:                  # run sequentially (order = list position)
  - name: deploy
    configuration:
      image: alpine:latest
      script: |
        echo deploying...

triggers:                         # OPTIONAL & additive (the deploy poll is implicit)
  - name: redeploy
    target: { type: processing, processingStepName: deploy }
```

Validation rejects the whole reconcile on: incompatible `apiVersion`, duplicate step names, a
trigger referencing an unknown processing step, both `script` and `scriptFile` set, or a
`$SECRET:NAME` that isn't declared in `secrets:`. A broken or unfetchable config **holds off the
build** for that cycle (config-before-build), so bad config never ships on stale code.

This repo dogfoods the feature: see [`.pipelines/config.yaml`](.pipelines/config.yaml). To
bootstrap or recreate the bound pipeline, use the `setup-pipeline` skill.

## Project Structure

```
src/Olve.Pipelines/                             # API application (minimal API, AOT)
├── Configuration/                               # Auth, telemetry, JSON, host, storage config
├── Health/                                      # Health check endpoint
├── Jobs/                                        # Job scheduling, obsoletion, cancellation
├── Kubernetes/                                  # K8s client, secrets, job specs
├── Pipelines/                                   # Pipeline CRUD, events
│   ├── Building/                                # ArtifactBundle entity and endpoints
│   ├── Polling/                                 # Poll trigger background service
│   ├── Processing/                              # ProcessingStep entity, service, endpoints
│   └── Production/                              # ProductionStep entity, service, endpoints
├── Shared/                                      # EntityStore, AttachmentStore, events, persistence
└── Dockerfile                                   # Multi-stage build (AOT, chiseled)
test/Olve.Pipelines.UnitTests/                   # Unit tests (TUnit + Rocks)
test/Olve.Pipelines.IntegrationTests/            # Integration tests (TUnit + Testcontainers)
frontend/                                        # Dashboard UI (Lit + Vite + TypeScript)
clients/Olve.Pipelines.Client/                   # Generated C# client (Refitter source gen)
clients/olve-pipelines-client-ts/                # Generated TypeScript client (Kiota)
helm/                                            # Helm chart for Kubernetes
tools/version.cs                                 # CalVer versioning script
```

## Endpoints

### Pipelines

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines?name=<name>` | Create pipeline |
| GET | `/api/pipelines` | List pipelines |
| GET | `/api/pipelines/summary` | List pipelines with per-step health (for the list page; one round-trip) |
| GET | `/api/pipelines/{id}` | Get pipeline |
| DELETE | `/api/pipelines/{id}` | Delete pipeline (cascades) |
| POST | `/api/pipelines/{id}/trigger/production` | Trigger production jobs |
| GET | `/api/pipelines/{id}/processing/promotions` | List per-step promotion (blocked) state |

### Production Steps

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines/{pipelineId}/production` | Create production step |
| GET | `/api/pipelines/{pipelineId}/production` | List production steps |
| GET | `/api/production-steps/{stepId}` | Get production step |
| DELETE | `/api/production-steps/{stepId}` | Delete production step |
| PUT | `/api/production-steps/{stepId}/configuration` | Set step configuration |
| GET | `/api/production-steps/{stepId}/configuration` | Get step configuration |
| DELETE | `/api/production-steps/{stepId}/configuration` | Remove step configuration |

### Processing Steps

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines/{pipelineId}/processing` | Create processing step |
| GET | `/api/pipelines/{pipelineId}/processing` | List processing steps (ordered) |
| GET | `/api/processing-steps/{stepId}` | Get processing step |
| DELETE | `/api/processing-steps/{stepId}` | Delete processing step |
| PUT | `/api/processing-steps/{stepId}/order` | Update step order |
| PUT | `/api/processing-steps/{stepId}/configuration` | Set step configuration |
| GET | `/api/processing-steps/{stepId}/configuration` | Get step configuration |
| DELETE | `/api/processing-steps/{stepId}/configuration` | Remove step configuration |
| GET | `/api/processing-steps/{stepId}/promotion` | Get promotion gate (`{ blocked }`) |
| PUT | `/api/processing-steps/{stepId}/promotion` | Block/unblock promotion (operational; open when bound) |
| POST | `/api/processing-steps/{stepId}/re-promote` | Redrive the step's last bundle (refused if blocked or never run) |

### Artifact Bundles

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/artifact-bundles` | List artifact bundles |
| GET | `/api/artifact-bundles/{bundleId}` | Get artifact bundle |

### Jobs

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/jobs` | List all jobs |
| GET | `/api/jobs/{id}` | Get job |
| GET | `/api/jobs/queue` | Get scheduled job queue |
| POST | `/api/jobs/{id}/cancel` | Cancel job |
| DELETE | `/api/jobs/{id}` | Delete job |

### Triggers

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines/{pipelineId}/triggers` | Create trigger |
| GET | `/api/pipelines/{pipelineId}/triggers` | List triggers |
| GET | `/api/triggers/{triggerId}` | Get trigger |
| DELETE | `/api/triggers/{triggerId}` | Delete trigger |
| POST | `/api/webhooks/{triggerId}` | Fire webhook trigger |

Trigger types:
- **production** — triggers all production steps
- **processing** — triggers a specific processing step with an artifact bundle
- **poll** — periodically GETs a URL, extracts a JSON value via dot-path, and triggers production when the value changes. Supports `$SECRET:NAME` references in headers resolved from pipeline K8s secrets.

### Secrets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/secrets` | List secret names |
| PUT | `/api/pipelines/{pipelineId}/secrets/{name}` | Set secret |
| DELETE | `/api/pipelines/{pipelineId}/secrets/{name}` | Delete secret |

### GitOps Binding

See [GitOps Configuration](#gitops-configuration). `branch` defaults to `main`, `path` to
`.pipelines`; `credentialsSecret` names the key in the pipeline's k8s secret holding the repo
read token.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/pipelines/with-repo` | Create a pipeline already bound to a repo |
| POST | `/api/pipelines/{id}/binding` | Bind an existing pipeline to a repo |
| GET | `/api/pipelines/{id}/binding` | Get the binding |
| GET | `/api/pipelines/{id}/binding/status` | Reconcile result/problems + live secret set/unset |

### Other

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/health` | Health check |

## Build & Test

```bash
dotnet build                                                   # Build
dotnet test                                                    # Unit tests only
dotnet test -p:RunIntegrationTests=true -p:RunUnitTests=false  # Integration tests only (requires Docker)
dotnet test -p:RunIntegrationTests=true                        # All tests
dotnet run --project src/Olve.Pipelines                        # Run locally
```

Integration tests use [Testcontainers](https://dotnet.testcontainers.org/) to build and run the app in Docker with a MinIO sidecar for S3 storage.

## Configuration

Sources in priority order (highest wins):

1. CLI args (`--Port 9090`)
2. User secrets (`dotnet user-secrets set "Key" "value"`)
3. Environment variables
4. `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `Host` | `localhost` | Listen address |
| `Port` | `5000` | Listen port |
| `Auth:Authority` | — | OIDC authority |
| `Auth:Audience` | `olve-pipelines` | JWT audience |
| `Auth:SigningKey` | — | Local HS256 key (bypasses OIDC, for dev) |
| `Storage:Endpoint` | — | S3-compatible endpoint (MinIO) |
| `Storage:AccessKey` | — | S3 access key |
| `Storage:SecretKey` | — | S3 secret key |
| `Storage:Bucket` | — | S3 bucket name |
| `Kubernetes:Namespace` | — | K8s namespace for jobs and secrets |
| `OpenTelemetry:Endpoint` | — | OTLP endpoint (null = disabled) |

## Client Generation

### C# ([Refitter](https://refitter.github.io/))

The `clients/Olve.Pipelines.Client/` project uses the Refitter source generator to produce a typed Refit interface from `api.json` at build time. The `.refitter` config uses `returnIApiResponse: true` so error responses don't throw exceptions.

### TypeScript ([Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview))

```bash
dotnet tool restore
dotnet kiota generate -l typescript -d api.json -c OlvePipelinesApiClient -o clients/olve-pipelines-client-ts/src -n OlvePipelinesApi
```

## Versioning

```bash
dotnet run tools/version.cs                                          # 0.0.0-dev+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42                  # 2026.3.28.42+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42 --rid linux-x64  # artifact name
```

## Running

```bash
# Local
dotnet run --project src/Olve.Pipelines

# Kubernetes
helm install olve-pipelines helm/
```
