# TODO

## Pipeline secrets

- [ ] API: `PUT /api/pipelines/{id}/secrets/{name}` (set)
- [ ] API: `DELETE /api/pipelines/{id}/secrets/{name}` (remove)
- [ ] API: `GET /api/pipelines/{id}/secrets` (list names only)
- [ ] Frontend: secret management UI

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
