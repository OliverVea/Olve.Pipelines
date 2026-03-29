# TODO

## Done

- [x] Pipeline, PipelineSource, PipelineBuilder, PipelineArtifact, ProcessingStep, Verification entities with CRUD
- [x] EntityStore with events, indexes, and DI-based seeding
- [x] AttachmentStore for composition-based step implementations
- [x] Step implementations: GitHubSource, HardcodedSource, ScriptBuilder, ScriptProcessing, ScriptVerification
- [x] Type enum on each entity (None → specific type when attachment is set)
- [x] Manual trigger endpoints (sourcing, building, processing) with placeholder bundles
- [x] SourceBundle and ArtifactBundle entities with in-memory storage
- [x] IBundleStore interface with S3BundleStore implementation (upload/download/list bundles)
- [x] BundlePersistenceService loads bundles from S3 on startup
- [x] Integration test infrastructure (AppFixture with Testcontainers + MinIO)

## Kubernetes Job runner (next)

All pipeline steps (sourcing, building, processing, verification) execute as K8s Jobs.

- [ ] K8s client integration (in-cluster auth)
- [ ] Job runner: create K8s Job from step config (image, script, env vars)
- [ ] Job runner: mount pipeline secrets (K8s Secrets) into Jobs
- [ ] Job runner: pass input bundle reference (S3 key) to Job
- [ ] Job runner: capture output bundle from Job and upload to S3
- [ ] Job runner: stream/collect Job logs
- [ ] Job runner: track Job status → update bundle status (Pending/Completed/Failed)
- [ ] Wire trigger endpoints to use Job runner instead of placeholders

## Pipeline secrets

- [ ] K8s Secret per pipeline (e.g. `olve-pipeline-{id}`)
- [ ] API: `PUT /api/pipelines/{id}/secrets/{name}` (set)
- [ ] API: `DELETE /api/pipelines/{id}/secrets/{name}` (remove)
- [ ] API: `GET /api/pipelines/{id}/secrets` (list names only)
- [ ] Auto-mount pipeline secret into all Jobs for that pipeline

## Automatic downstream triggering

- [ ] Sourcing completion triggers building automatically
- [ ] Building completion triggers first processing step automatically
- [ ] Processing step success (all verifications pass) triggers next processing step
- [ ] Verification failure blocks promotion until re-triggered and passes

## Persistence

- [ ] S3 storage for pipeline configuration (replace current persistence service)

## Source change detection

- [ ] GitHub webhook endpoint to trigger sourcing on push
- [ ] Polling option for sources without webhook support

## Frontend

- [ ] Show pipeline phases with trigger buttons
- [ ] Display bundle history per phase
- [ ] Show bundle status (pending/completed/failed)
- [ ] Show verification results per processing step

## Typed step templates (future)

Templates generate script + image + env for K8s Jobs. Scripts remain as the "custom" fallback.

- [ ] Build templates: .NET build, Helm chart, static frontend, Docker image
- [ ] Processing templates: K8s deployment, publish to store
- [ ] Verification templates: health check, integration test

## Cleanup

- [ ] Processing step ordering (currently unordered)
