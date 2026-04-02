# TODO

## Job execution

- [ ] Submit ProductionJob to Kubernetes (create K8s Job per production step, collect outputs into ArtifactBundle)
- [ ] Submit ProcessingJob to Kubernetes (create K8s Job with ArtifactBundle as input)
- [ ] Job status polling / K8s watch to update job status (InProgress -> Done/Failed)
- [ ] Signal protocol for steps to communicate back (progress, verification results, artifact labels)

## Automatic downstream triggering

- [ ] Production completion triggers first processing step automatically
- [ ] Processing step success triggers next processing step
- [ ] Processing trigger endpoint (`POST /api/pipelines/{id}/trigger/processing/{stepId}`)

## Source change detection

- [ ] GitHub webhook endpoint to trigger production on push
- [ ] Polling option for sources without webhook support

## Typed step templates (future)

Templates pre-populate `(image, script, env)` for common patterns. Scripts remain as the "custom" fallback.

- [ ] Production templates: .NET build, Helm chart, Docker image build, static frontend
- [ ] Processing templates: K8s deployment, Helm upgrade, publish to registry
- [ ] Template attachment stores (composition, extensible by addition)

## Cleanup

- [ ] Regenerate TypeScript client (stale, still references old sourcing/building/verification endpoints)
- [ ] Remove `frontend/` directory (no longer served)
- [ ] Bundle upload/download endpoints (currently only metadata is served)
- [ ] Periodic background save for crash resilience (ConfigurationPersistenceService TODO)
