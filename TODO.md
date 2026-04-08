# TODO

## Job execution

- [x] Submit ProductionJob to Kubernetes (KubernetesJobExecutor)
- [x] Submit ProcessingJob to Kubernetes (KubernetesJobExecutor)
- [x] Job status polling (KubernetesJobExecutor polls K8s API every 5s until completion)
- [ ] Signal protocol for steps to communicate back (progress, verification results, artifact labels)

## Automatic downstream triggering

- [x] Production completion triggers first processing step automatically (DownstreamTriggerService)
- [x] Processing step success triggers next processing step (DownstreamTriggerService)
- [x] Processing trigger endpoint (`POST /api/pipelines/{id}/trigger/processing`)

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
