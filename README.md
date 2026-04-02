# Olve.Pipelines

A lightweight CD pipeline configuration and orchestration service for a homelab. Manages pipeline definitions with production steps (parallel builds) and processing steps (sequential deployments), executed as Kubernetes Jobs.

## Pipeline Model

```
Production [N steps, parallel] ──(ArtifactBundle)──> Processing 1 ──> ... ──> Processing N [sequential]
```

- **Production steps** run in parallel. Each produces output to `bundle/<step-name>/`. Combined output = ArtifactBundle.
- **Processing steps** run sequentially. Each receives the full ArtifactBundle.
- Every step is configured as `(image, script, env)` and executed as a Kubernetes Job.

## Project Structure

```
src/Olve.Pipelines/                             # API application (minimal API, AOT)
├── Configuration/                               # Auth, telemetry, JSON, host, storage config
├── Health/                                      # Health check endpoint
├── Jobs/                                        # Job scheduling, obsoletion, cancellation
├── Kubernetes/                                  # K8s client, secrets, job specs
├── Pipelines/                                   # Pipeline CRUD, events
│   ├── Building/                                # ArtifactBundle entity and endpoints
│   ├── Processing/                              # ProcessingStep entity, service, endpoints
│   └── Production/                              # ProductionStep entity, service, endpoints
├── Shared/                                      # EntityStore, AttachmentStore, events, persistence
└── Dockerfile                                   # Multi-stage build (AOT, chiseled)
test/Olve.Pipelines.UnitTests/                   # Unit tests (TUnit + Rocks)
test/Olve.Pipelines.IntegrationTests/            # Integration tests (TUnit + Testcontainers)
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
| GET | `/api/pipelines/{id}` | Get pipeline |
| DELETE | `/api/pipelines/{id}` | Delete pipeline (cascades) |
| POST | `/api/pipelines/{id}/trigger/production` | Trigger production jobs |

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

### Secrets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/pipelines/{pipelineId}/secrets` | List secret names |
| PUT | `/api/pipelines/{pipelineId}/secrets/{name}` | Set secret |
| DELETE | `/api/pipelines/{pipelineId}/secrets/{name}` | Delete secret |

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
