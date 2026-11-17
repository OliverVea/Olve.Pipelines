# Olve.Pipelines v2: roadmap

What is being worked on next, agreed with Oliver, and the known gaps. The design it builds on is
[pipelines-v2.md](pipelines-v2.md), [pipeline-config.md](pipeline-config.md)
and [external-inputs.md](external-inputs.md). What stands between it and other people running it: [public-readiness.md](public-readiness.md).

## State

- **v2 is the only Olve.Pipelines.** Prod serves on `pipelines-private.ovea.pro`, beta on
  `pipelines-beta.ovea.pro`, webhooks on `pipelines-hooks(-beta)`. Development is on `main`; every
  push deploys beta, runs `beta-test` (the unit suite plus beta's `v2 test fixture`, whose steps wait
  for a callback and a poll), then deploys prod.
- **No homelab dependencies**: sign-in is the bundled Dex (GitHub connector,
  `auth.allow.users: [OliverVea]` enforced by the server, device flow for `pl`), cluster access is
  `InCluster`, telemetry is standard OTLP (`none` on both instances).
- **Prod's config document** is [prod-config.example.yaml](prod-config.example.yaml): the server
  image is built to `registry.ovea.pro` and released to ghcr by digest, the harness goes to ghcr
  through the Hub cache, npm publishing is on (`next` on publish, `beta` on promote),
  `build.runtime: jit`.
- **Every repo runs on v2**: Olve.Homelab, Olve.Authentik, Olve.AgentRuntimeManager, Olve.Trains,
  crafty, QuestionBank, homelab-dashboard, Olve.SlimeRepublics, Olve.Template.Api, and the deployer.
  crafty's `deploy` and `verify` are blocked because crafty itself is taken down (no release in
  `apps`, only its backups PVC); unblocking both brings it back.
- **Retention** deletes for real on both instances: prod on the defaults (183 days and keep 20),
  beta on 14 days and keep 3, the deployer on its own `retention` (30 days, keep 50).
- **Local checks**: `tools/check.sh` runs what the deploy checks; a pre-push hook gates `git push`
  on it.
- Shipped in 2.1: the spec-first API, `pl dev run --from`, the cached `imageBuild` (`contentTag`
  hashes the build recipe), step destination links, every quality-inventory item, step reliability,
  config editing as a form generated from the schema, matrix steps
  ([matrix-builds.md](matrix-builds.md)), and secret masking in step logs.

## Next

1. **Caching.** Spend less time redoing work that hasn't changed. Start from where the time goes
   (`pl run critical-path`), then:
   - **Output caching**: a step whose code hash, step version, pinned config and input match an
     earlier succeeded attempt reuses that attempt's output and files instead of running. Opt-in per
     step and off by default, since deploys must always run. A key on the whole input never hits for
     a source-based step (`sha` and `tree` change every commit), so a step can key on a mapped
     subset, recorded as tagged mappings so the service computes it without TypeScript (e.g. content
     hashes of path sets from `gitHubSource`). A reused attempt is a succeeded attempt that
     references the earlier output; retention keeps runs whose files are still referenced. The
     trade-off in "the same code": `codeHash` never reuses a stale result but clears every step's
     cache on any code change; a step naming what it depends on keeps more hits, but a wrong list
     reuses stale results with no error.
   - **Build caching**: `imageBuild` reuses kaniko layers from `cacheRepo` (by default next to a
     pushed image).
   - **Dependency caches are not planned.** In a gVisor step pod, downloading is the small part of
     an install: `npm ci` of the frontend takes 5.2 s cold and 3.4 s with nothing to download,
     `dotnet restore` of the server 9.3 s and 5.3 s. A package proxy would save 2 to 4 s a step;
     the rest is unpacking, which only skipping the step saves.
   - **BuildKit** in place of kaniko, which is no longer developed: faster builds, parallel stages
     and `RUN --mount=type=cache`. Rootless BuildKit needs user namespaces and relaxed
     seccomp/AppArmor, which the gVisor step pods and PSS baseline don't give, so it is likely a
     long-running builder service with its own volume that steps send builds to. That is an
     infrastructure and security decision first.
2. **Packaged steps** with a public `defineStep(def, impl)`:
   - Built-in steps use the same API: internal tooling = public tooling. The closed `StepKind`
     list goes away.
   - Every step is versioned by its bundled code + config.
   - Steps carry provenance (package, version, author, repository, docs; optionally verified npm
     provenance).

## Deadline

- **npm staged publishing**, before late January 2027. `NPM_TOKEN` is a package-scoped
  direct-publish token that expires then, when npm also removes direct publishing for such tokens.
  Trusted publishing is out (GitHub Actions, GitLab and CircleCI only, no self-hosted runners). The
  options: stage once in `promote` with `npm stage publish --tag beta` (Oliver approves each SDK
  version on npmjs.com), or a self-hosted registry carrying every version with rare, deliberate npm
  staged releases.

## Parked

Not planned; picked up only when a need shows.

- Config UI: preview a change before saving, the effective pipeline on the overview (which `select`
  branch is chosen), the run's pinned config in the run view, every case in the manifest view.
- Manifest preview of a `.pipelines/` change before pushing.
- Event-stream filtering, OpenObserve metrics, environments/tags.
- Problem types lifted into Olve.MinimalApi.
- External inputs' open ideas (`at`, another run): [external-inputs.md](external-inputs.md).
- Per-pipeline ServiceAccounts (steps declare their namespaces; a Role scoped to them), modelled
  permissions and roles.
- Approval and rollback, rollback monitoring: a deploy step declares what healthy means and for how
  long (`monitor: { forSeconds, checks, onBreach: "rollback" }`, checks as typed library pieces);
  the service runs the checks after the step succeeds, and a breach reruns the deploy with the last
  healthy deployment's input. Builds on environments.
- Teardown: every step states its teardown (library deploys bring theirs: `helm uninstall`, compose
  stop), and deleting a pipeline runs them forward in stage order on the active version, each fed the
  step's last successful output; data stays.
- A Triggers page in the UI.
- A default `{repo, sha}` mapping for `gitHubSource` (every pipeline writes the same mapper).
- Docs and v1-residue cleanup (e.g. the `olve-pipelines-minio` secret name).
- Pluggable runtimes (`kubernetes | docker | process`), Gitea / Forgejo sources.
- Bringing crafty back: unblock `deploy` and `verify`, then run it.

## Known gaps

- Switching back to a version whose binary retention removed fails at launch (only a version no
  retained run used, after the build grace).
- A failure handler whose dispatch is lost to a crash between the run failing and its Job being
  created is not retried (handlers are best-effort).
- Step reliability: the failure kind is counted but not shown on an attempt; matching executions
  across runs needs upstream files to be byte-identical, so non-deterministic artifacts (tarballs
  with mtimes) leave later steps unmatched between runs.
- `JobQueueService` and `JobLogService` are named after Jobs but serve attempts.

## Design rules agreed

- Never branch on config inside a step. Use `select`, so each step declares only what it uses.
- Configure a step's implementation through a small, stable contract (thin step plus a service
  behind it), so a bad implementation is fixed without editing pipelines.
- Verify early, build once, promote the same artifact.
- Defaults should just work for a fresh self-hosted install, but secure by default comes first.
