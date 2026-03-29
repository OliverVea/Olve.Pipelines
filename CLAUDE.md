# CLAUDE.md

See [README.md](README.md) for project structure, endpoints, configuration, CI examples, and client generation.

## Design

Olve.Pipelines is a lightweight CD pipeline configuration and orchestration service for a homelab.

### Pipeline Flow

A pipeline has three phases that execute in sequence:

```
Sourcing ──(SourceBundle)──> Building ──(ArtifactBundle)──> Processing 1 ──> ... ──> Processing N
```

Each phase can be triggered independently ("run this step with whatever is at your input").

### Entities

- **Pipeline** — top-level entity grouping all configuration for a single CD workflow.
- **PipelineSource** — where code/data comes from (e.g. GitHub repo). A pipeline has many sources. During sourcing, all sources are snapshotted into a **SourceBundle**.
- **PipelineBuilder** — defines how to build. A pipeline has many builders. Each builder runs a script and writes output files to `bundle/<builder-name>/`. The combined output of all builders is an **ArtifactBundle**.
- **ProcessingStep** — a post-build action (e.g. deploy to staging). A pipeline has an ordered list of processing steps. Each takes an ArtifactBundle, runs its action, then runs verifications before promoting to the next step.
- **Verification** — a check that gates a processing step (e.g. health check). If any fails, promotion is blocked.

### Bundles

- **SourceBundle** — a snapshot of all sources at a point in time. Produced by sourcing.
- **ArtifactBundle** — the collected build outputs as a zipped directory in S3: `bundle/<builder-name>/<files>`. Produced by building, consumed by processing steps.

### Step Configuration

Step implementations (scripts, GitHub config, etc.) are attached via composition, not inheritance. Each gets a dedicated sub-resource endpoint (e.g. `PUT /sources/{id}/github`, `PUT /builders/{id}/script`).

### Configuration vs Execution

Configuration defines *what* each step does. Execution (source polling, build runners, artifact storage) is triggered via `/trigger/sourcing`, `/trigger/building`, `/trigger/processing/{id}`.

### Execution Runners

Sourcing, building, and processing steps run as external processes (shell scripts, docker commands, etc.). Runner infrastructure is TBD — needs decisions on where runners execute (local process, Kubernetes job, etc.), how they report status, and how to configure runner targets per environment.

## Commands

```bash
dotnet build                                                # Build
dotnet test                                                 # Unit tests only
dotnet test -p:RunIntegrationTests=true -p:RunUnitTests=false  # Integration tests only
dotnet test -p:RunIntegrationTests=true                     # All tests
dotnet run --project src/Olve.Pipelines                  # Run locally
```

## Conventions

- .NET 10, C# with file-scoped namespaces, nullable enabled, implicit usings
- Package versions managed centrally in `Directory.Packages.props` — do not add `Version` attributes in csproj files
- Local config via `dotnet user-secrets`, not appsettings files
- OpenAPI spec `api.json` is generated on build by `Microsoft.Extensions.ApiDescription.Server`
- AOT publishing enabled — do not add AOT-incompatible libraries (e.g. EF Core)
- Namespaces are always plural/more general than contained types (e.g. `Pipeline` type in `Pipelines` namespace)
- Storage via S3-compatible MinIO (minio.ovea.pro) — JSON files for persistence, zipped directories for bundles
- Frontend is vanilla TS + Vite, served as static files from the .NET app. API under `/api`, frontend at `/`
- TypeScript client generated from OpenAPI via Kiota

## References

- [Olve.* packages](https://olivervea.github.io/Olve.Utilities/) — index of all Olve packages
  - [Olve.Results](https://olivervea.github.io/Olve.Utilities/src/Olve.Results/README.html) — non-throwing result types for error handling
  - [Olve.Validation](https://olivervea.github.io/Olve.Utilities/src/Olve.Validation/README.html) — input validation built on Olve.Results
  - [Olve.MinimalApi](https://olivervea.github.io/Olve.Utilities/src/Olve.MinimalApi/README.html) — result-to-HTTP mapping for minimal APIs
  - [Olve.Utilities](https://olivervea.github.io/Olve.Utilities/src/Olve.Utilities/README.html) — identifiers, collections, graph types
  - [Olve.Results.TUnit](https://olivervea.github.io/Olve.Utilities/src/Olve.Results.TUnit/README.html) — TUnit assertions for Result types (`Succeeded()`, `Failed()`, etc.)
- [TUnit](https://tunit.dev/docs/intro) — test framework, uses `await Assert.That(...)` fluent syntax (not xUnit/NUnit)
- [Rocks](https://raw.githubusercontent.com/JasonBock/Rocks/refs/heads/main/docs/Overview.md) — source-generated mocking (AOT-compatible)
- [Refitter](https://refitter.github.io/articles/refitter-file-format.html) — C# client source gen from OpenAPI via Refit (.refitter file format)
- [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview) — TypeScript client gen from OpenAPI
