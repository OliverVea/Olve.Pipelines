# Olve.Pipelines v2: roadmap

What is being worked on next, agreed with Oliver. The detail behind each item lives in
[pipelines-v2-plan.md](pipelines-v2-plan.md), [pipeline-config.md](pipeline-config.md),
[external-inputs.md](external-inputs.md), [review-2026-10-24.md](review-2026-10-24.md) and
[quality-inventory.md](quality-inventory.md).

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
  and config editing as a form generated from the schema.

## Next

1. **Matrix builds.** One step run once per entry of a list (platforms, targets, versions), shown
   as one step with its cells in the UI. Starts with a UI mock.
2. **Caching.** Spend less time redoing work that hasn't changed. Start from where the time goes
   (`pl run critical-path`), then:
   - **Output caching**: a step whose code hash, step version, pinned config and input match an
     earlier succeeded attempt reuses that attempt's output and files instead of running.
   - **Build caching**: `imageBuild` reuses kaniko layers from `cacheRepo` (by default next to a
     pushed image). Still open: dependency caches (bun, npm, NuGet) for step pods and the
     build/describe Job, so a cold pod doesn't re-download them.
   - **BuildKit** in place of kaniko, which is no longer developed: faster builds, parallel stages
     and `RUN --mount=type=cache`. Rootless BuildKit needs user namespaces and relaxed
     seccomp/AppArmor, which the gVisor step pods and PSS baseline don't give, so it is likely a
     long-running builder service with its own volume that steps send builds to. That is an
     infrastructure and security decision first.
3. **Secret masking in logs.** A declared secret's value never shows in a step's log, live or
   stored.
4. **Packaged steps** with a public `defineStep(def, impl)`:
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
- Approval and rollback, rollback monitoring.
- Docs and v1-residue cleanup (e.g. the `olve-pipelines-minio` secret name).
- Pluggable runtimes (`kubernetes | docker | process`), Gitea / Forgejo sources.
- Bringing crafty back: unblock `deploy` and `verify`, then run it.

## Design rules agreed

- Never branch on config inside a step. Use `select`, so each step declares only what it uses.
- Configure a step's implementation through a small, stable contract (thin step plus a service
  behind it), so a bad implementation is fixed without editing pipelines.
- Verify early, build once, promote the same artifact.
- Defaults should just work for a fresh self-hosted install, but secure by default comes first.
