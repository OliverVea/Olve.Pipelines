# Olve.Pipelines v2: roadmap

The milestone scope agreed with Oliver on 2026-10-24, refreshed 2026-10-27. The detail behind each
item lives in [pipelines-v2-plan.md](pipelines-v2-plan.md), [pipeline-config.md](pipeline-config.md),
[external-inputs.md](external-inputs.md), [review-2026-10-24.md](review-2026-10-24.md) and
[quality-inventory.md](quality-inventory.md).

## State on 2026-10-27

- **v2 is the only Olve.Pipelines.** v1 was uninstalled on 2026-10-25; prod serves on
  `pipelines-private.ovea.pro`, beta on `pipelines-beta.ovea.pro`, webhooks on `pipelines-hooks(-beta)`.
  Development is on `main`; every push deploys beta, runs `beta-test` (the unit suite plus beta's
  `v2 test fixture`, whose steps wait for a callback and a poll), then deploys prod.
- **No homelab dependencies** since 2026-10-26: sign-in is the bundled Dex (GitHub connector,
  `auth.allow.users: [OliverVea]` enforced by the server, device flow for `pl`), cluster access is
  `InCluster`, telemetry is standard OTLP (`none` on both instances). Authentik and OpenBao are gone
  from the server and the chart.
- **Prod's config document** is [prod-config.example.yaml](prod-config.example.yaml) (config #13):
  the server image is built to `registry.ovea.pro` and released to ghcr by digest, the harness goes
  to ghcr through the Hub cache, npm publishing is on (`next` on publish, `beta` on promote),
  `build.runtime: jit`.
- **Every repo runs on v2**: Olve.Homelab, Olve.Authentik, Olve.AgentRuntimeManager, Olve.Trains,
  crafty, QuestionBank, homelab-dashboard, Olve.SlimeRepublics, Olve.Template.Api, and the deployer.
  crafty's `deploy` and `verify` are blocked since 2026-10-25 because crafty itself is taken down
  (no release in `apps`, only its backups PVC); unblocking both brings it back.
- **External inputs** shipped 2026-10-26: `external(model, { from: [person() | callback() | poll()] })`,
  `pl input`, machine tokens, bindings that don't poll.
- **Retention** deletes for real on both instances since 2026-10-27: prod on the defaults (183 days
  and keep 20), beta on 14 days and keep 3, the deployer on its own `retention` (30 days, keep 50;
  SDK 2.0.0-beta.18).
- **Local checks**: `tools/check.sh` runs what the deploy checks (`--changed`, `--full`, `--fix`); a
  pre-push hook gates `git push` on it. The SDK, its examples and `.pipelines` are under prettier,
  the server under `dotnet format`, and the deploy's `build-sdk` and `test` steps enforce both.
- Parked branch: `wip/compose-deploy` (the compose deploy target plus deployer, a single local
  instance that redeploys itself). `rollout/phase-1-dropped` is deleted.

## 2.0: done

Prod auth on Dex with the allow-list, ghcr harness and server images, npm publishing, all v1 repos
migrated, `v2` promoted to `main`, v1 retired. Shipped 2026-10-24 to 2026-10-26; the per-item
record is in the plan's Track E.

## 2.1: now

0. **Spec-first API (TypeSpec)**, decided 2026-10-27: [spec-first.md](spec-first.md). The emitter
   and runtime move from ARM to Olve.Utilities first, then the API migrates area by area with a
   Hey API frontend client, then `pl` is rewritten in TypeScript, then Olve.Template.Api follows.
1. ~~**Retention live on prod.**~~ Done 2026-10-27: the dry-run sweep would have deleted two
   leftover result archives (572 bytes) and one orphaned URL Secret, no runs and no binaries; the
   deployer declares `retention` (a self-deploy run is ~55 MB, so 183 days of it would outgrow the
   100 Gi Garage) and `values-v2-prod.yaml` sets `Retention__DryRun=false`. Watch the first sweeps.
2. **npm staged publishing.** `NPM_TOKEN` is a package-scoped direct-publish token from 2026-10-25
   that expires in late January 2027, when npm also removes direct publishing for such tokens.
   Change `publish-sdk` to stage, have Oliver approve each version on npmjs.com, then move to a
   stage-only token. Trusted publishing is out: it needs GitHub Actions, which this repo doesn't use.
3. **`pl dev run --from`** (plan C2, inventory U6): hidden from help since 3187dc8; the run API it
   needs exists.
4. **Quality inventory**, the unmarked items: U2 (`pl wait push` matches on an input field named
   `sha` over the newest 15 runs), F2 (the log tail stops following after a clean disconnect), F3,
   F4, T3 to T7 (CLI, `devMain` and frontend tests; `.Value!` in tests), D3 to D6.
5. **Bring crafty back**, when wanted: unblock `deploy` and `verify`, then run it.
6. Small follow-ups found on the way:
   - `publish-*` steps declare `REGISTRY_SECRETS` whole; with `select` each would declare only the
     selected registry's secret.
   - Fix the `contentTag` cache key: it must cover build options, not just the context.
   - A cached `imageBuild` (`contentTag` found the image, kaniko skipped) reads as a plain success;
     the step output or `pl run get` should say the build was skipped.
   - Pin `KANIKO_IMAGE` (today `gcr.io/kaniko-project/executor:debug`, floating).
   - Beta runs the new harness before prod now (the fixture), so that item is done.
7. **The step destination link** (a deploy step links to what it deployed).
8. **A real config UI** (2.1 or 2.2). Oliver lists the requirements first; then we iterate on a
   local frontend (`npm run dev:v2beta`) against beta. Agreed starting points for `select`:
   - The overview shows the *effective* pipeline: steps from a select carry a
     `target.prod.kind = ssh` chip, and an optional "show all variants" toggle draws the
     unselected cases greyed.
   - The run view shows the run's pinned snapshot, with a note when current config would select
     differently.
   - The config page previews a discriminator change before saving (steps, secrets, cluster
     access, whether the version would go pending), and the pending banner names the branch.
   - The manifest view lists every case.

## 2.2

The plan doc's candidates: manifest preview, event-stream filtering, OpenObserve metrics, environments/tags, problem types lifted into
Olve.MinimalApi. External inputs' open ideas (`at`, another run) are in
[external-inputs.md](external-inputs.md).

## 2.3 / 2.4

- **Per-pipeline ServiceAccounts.**
  - Today a step opts in with `clusterAccess`, and every opted-in step shares
    `Kubernetes:StepServiceAccount`.
  - Next: steps declare the namespaces they need, and the server binds a per-pipeline
    ServiceAccount and Role scoped to them.
- **Modelled permissions** (roles, per-pipeline rights) on top of the allowlist.
- **Most of the 2026-10-24 review:**
  - secret masking in logs
  - roles
  - docs and v1-residue cleanup (e.g. the `olve-pipelines-minio` secret name)
  - approval and rollback
- Output caching and rollback monitoring (plan doc).

## 3.0

- **Pluggable runtimes:** `kubernetes | docker | process (unsafe)` behind one abstraction (run,
  reattach, clean up, secret store). Kubernetes coupling to unpick:
  - `KubernetesJobExecutor` and the failure-handler launcher
  - build Jobs in `PipelineReconcileService`
  - `TriggerMappingService`
  - `PipelineSecretService` / `GitHubConfigSource`
  - `RetentionService`
- **Gitea / Forgejo sources.**
- **Packaged steps** with a public `defineStep(def, impl)`:
  - Built-in steps use the same API: internal tooling = public tooling. The closed `StepKind`
    list goes away.
  - Every step is versioned by its bundled code + config.
  - Steps carry provenance (package, version, author, repository, docs; optionally verified npm
    provenance).

## Design rules agreed

- Never branch on config inside a step. Use `select`, so each step declares only what it uses.
- Configure a step's implementation through a small, stable contract (thin step plus a service
  behind it), so a bad implementation is fixed without editing pipelines.
- Verify early, build once, promote the same artifact.
- Defaults should just work for a fresh self-hosted install, but secure by default comes first.
