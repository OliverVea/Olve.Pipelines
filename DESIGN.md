# Design

Olve.Pipelines is a lightweight CI/CD pipeline configuration and orchestration service for a homelab.

## Pipeline Flow

A pipeline has three phases that execute in sequence:

```
Sourcing ──(SourceBundle)──> Building ──(ArtifactBundle)──> Processing 1 ──(ArtifactBundle)──> ... ──> Processing N
```

Each phase can be triggered independently ("run this step with whatever is at your input").

## Terminology

### Pipeline

The top-level entity. Groups all configuration for a single CI/CD workflow.

### PipelineSource

Defines where code or data comes from (e.g. a GitHub repository). A pipeline has many sources. During **sourcing**, all sources are snapshotted together into a **SourceBundle**.

### PipelineBuilder

Defines how to build. A pipeline has many builders. During **building**, each builder takes a SourceBundle as input and produces named **Artifacts**. The combined output is an **ArtifactBundle**.

### PipelineArtifact

A named output of a builder (e.g. "Docker Image"). Belongs to a specific builder.

### ProcessingStep

A post-build action (e.g. deploy to staging, deploy to production). A pipeline has an ordered list of processing steps. Each step takes an ArtifactBundle as input and, on success, promotes it to the next step.

### Verification

A check that gates a processing step (e.g. health check, smoke test). A processing step has many verifications. If any verification fails, promotion is blocked until it passes.

### SourceBundle (future)

A snapshot of all sources at a point in time. Produced by the sourcing phase. Has an ID for traceability.

### ArtifactBundle (future)

The collected build outputs. Produced by the building phase and passed through processing steps. Has an ID for traceability.

## Configuration vs Execution

Currently, only **configuration** is modeled — entities define what each step does, not how it runs. Execution (source polling, build runners, S3 storage, artifact bundling) will be added later.

Step implementations (e.g. "run this script", "pull from GitHub") will be attached to steps via composition, not inheritance. Each step type gets a dedicated sub-resource endpoint (e.g. `PUT /sources/{id}/github`).
