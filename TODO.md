# TODO

## Done

- [x] Pipeline, PipelineSource, PipelineBuilder, PipelineArtifact, ProcessingStep, Verification entities with CRUD
- [x] EntityStore with events, indexes, and DI-based seeding
- [x] AttachmentStore for composition-based step implementations
- [x] Step implementations: GitHubSource, HardcodedSource, ScriptBuilder, ScriptProcessing, ScriptVerification
- [x] Type enum on each entity (None → specific type when attachment is set)
- [x] Manual trigger endpoints (sourcing, building, processing) with placeholder bundles
- [x] SourceBundle and ArtifactBundle entities with in-memory storage

## Bundle S3 storage (next)

- [ ] Upload SourceBundle metadata + contents to S3 on creation
- [ ] Upload ArtifactBundle metadata + contents to S3 on creation
- [ ] Load bundle history from S3 on startup
- [ ] S3 key scheme: `bundles/source/{bundleId}.json`, `bundles/artifact/{bundleId}.json`

## Real execution

- [ ] Sourcing: HardcodedSource returns its values, GitHubSource resolves HEAD commit SHA
- [ ] Building: ScriptBuilder runs shell script via Process.Start
- [ ] Processing: ScriptProcessing runs shell script via Process.Start
- [ ] Verification: ScriptVerification runs shell script, exit code = pass/fail
- [ ] Bundle status: Pending while running, Completed/Failed on finish
- [ ] Store execution output/logs on bundles
- [ ] Runner infrastructure: decide where runners execute (local process, K8s job, etc.) and how to configure runner targets per environment

## Automatic downstream triggering

- [ ] Sourcing completion triggers building automatically
- [ ] Building completion triggers first processing step automatically
- [ ] Processing step success (all verifications pass) triggers next processing step
- [ ] Verification failure blocks promotion until re-triggered and passes

## Persistence

- [ ] S3 storage for SourceBundles and ArtifactBundles
- [ ] S3 storage for pipeline configuration (replace current persistence service)
- [ ] Bundle contents: actual source snapshots and built artifacts stored in S3

## Source change detection

- [ ] GitHub webhook endpoint to trigger sourcing on push
- [ ] Polling option for sources without webhook support

## Frontend

- [ ] Show pipeline phases with trigger buttons
- [ ] Display bundle history per phase
- [ ] Show bundle status (pending/completed/failed)
- [ ] Show verification results per processing step

## Cleanup

- [ ] Update DESIGN.md to reflect current state (bundles are no longer future)
- [ ] Processing step ordering (currently unordered)
