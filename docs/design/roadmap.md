# Olve.Pipelines v2: roadmap

The milestone scope agreed with Oliver on 2026-10-24. The detail behind each item lives in
[pipelines-v2-plan.md](pipelines-v2-plan.md), [pipeline-config.md](pipeline-config.md) and
[review-2026-10-24.md](review-2026-10-24.md).

## State on 2026-10-25

- Typed config, `select`, the build-once/promote release flow, the ghcr-ready harness and the
  self-contained compose stack are live on v2 prod. The rollout was deployed from a local instance:
  run `#7`, then prod's own run `Olve.Pipelines v2#47` (v13, config #1).
- Prod's config document is `prod-config.example.yaml` (config #3): Authentik, npm off, the server
  image on registry.ovea.pro, the harness on ghcr.io (public, pushed with `GHCR_TOKEN`).
- Parked branches:
  - `wip/compose-deploy`: the compose deploy target plus deployer, which gives a single local
    instance that redeploys itself
  - `rollout/phase-1-dropped`: the old-server-compatible pipeline; no longer needed

## 2.0: v2 replaces v1

1. **Prod auth: switch to Dex.**
   - Dex GitHub connector, restricted with an app-level allowlist: `auth.allow.users: [OliverVea]`
     (+ `orgs`, `emails`), enforced by the server. Today `allow` exists in the schema only.
   - Dex must advertise the device flow (`pl login --device`).
   - Telemetry: if the deployed Dex supports client_credentials, use `oauth2` against Dex;
     otherwise use a static `header` token. Don't keep Authentik only for telemetry.
2. ~~**ghcr harness.**~~ Done 2026-10-24: config #2 pushes it with `GHCR_TOKEN` (run `#52`), the
   package is public, config #3 drops the pinned image (run `#53`).
3. ~~**npm.**~~ On since config #5 (2026-10-24): `next` on publish, `beta` on promote until a real 2.0.0.
   `NPM_TOKEN` expires 2026-10-29: replace it (scoped to the package) or move to trusted publishing.
4. **Retention live**, after checking the dry-run logs.
5. **`pl dev run --from`** (M2's open item).
6. **E3: migrate the remaining v1 repos, one at a time.** Order:
   1. crafty-k8s
   2. Olve.Trains
   3. Olve.AgentRuntimeManager
   4. Olve.Authentik, after prod is on Dex
   5. Olve.Homelab, last
7. **E4:** promote `v2` to `main`; retire the v1 instances, `config.yaml` and v1 `pl`.

Small follow-ups found on the way:
- `publish` should use `select`, so build/publish steps declare only the selected registry's secret.
- Fix the `contentTag` cache key: it must cover build options, not just the context.
- Measure build-server per kaniko instruction.
- Pin `KANIKO_IMAGE`.
- Done 2026-10-24: the server image is public on ghcr, copied from staging after verification
  (`publish.*.release`); the chart defaults to it.
- Beta never runs a step with the new harness before prod: beta-test could run a fixture pipeline on beta.

## 2.1

- **Wait steps** (spec §3.8, with callbacks). Design doc first.
- The **step destination link** (a deploy step links to what it deployed).
- **npm trusted publishing** (OIDC, provenance).
- **A real config UI** (2.1 or 2.2). Oliver lists the requirements first; then we iterate on a
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

The plan doc's candidates: TypeSpec API, gating steps, manifest preview, event-stream filtering,
OpenObserve metrics.

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
