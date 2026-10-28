# AGENTS.md

Agent-facing context for **building, structuring, deploying, and cold-boot-installing**
Olve.Pipelines. For design patterns and conventions see [CLAUDE.md](CLAUDE.md); for the
codebase see [CONTRIBUTING.md](CONTRIBUTING.md); for the product/usage reference the running instance's `/docs` (source
under [docs/setup](docs/setup)). Operations runbooks live in [docs/operations](docs/operations).

## How it's built

- .NET 10, Native-AOT published. Controller: `src/backend/Olve.Pipelines`. Operator CLI:
  `src/cli` (binary `pl`, Bun + TypeScript compiled with `bun build --compile`).
- `dotnet build` / `dotnet test`.
- SDK (`src/sdk/`, Bun + TypeScript): `bun install && bunx tsc --noEmit -p . && bun test`.
- In the v2 pipeline (`.pipelines/pipeline.ts`), the container is built with **kaniko**
  (`steps.imageBuild`, layer cache in `registry.ovea.pro`) while the unit tests run in parallel
  (`handlers/test.ts`). The image tag is the run's version stamp (`yyyy.MM.dd.<run>`, e.g.
  `2026.10.22.6`; `-attempt<n>` on reruns) — not a semver, so never stamp a .NET
  `AssemblyVersion`/`FileVersion` from it; use `InformationalVersion`.

## Structure

CLAUDE.md is the detailed map (service-layer architecture: EntityStore / AttachmentStore / event
hubs / domain services, plus conventions). Top level: `src/backend/Olve.Pipelines` (controller),
`src/cli/` (`pl`), `src/sdk/` (`@olivervea/pipelines`), `harness/` (step-pod init image),
`src/deploy/helm/` (chart + per-env values), `.pipelines/` (this repo's own pipeline), `.pipelines-test/` (v2
fixture), `src/frontend/` (Lit SPA), `docs/`.

## How it's deployed (self-deploy)

Olve.Pipelines deploys itself. **v2 (this branch):** the v2 prod instance
(`pipelines-private.ovea.pro`, release `olve-pipelines-v2` in `apps`, runners `olve-runners`)
runs the pipeline "Olve.Pipelines v2", bound to the `v2` branch, path `.pipelines`: `source` →
`build-server` ∥ `build-harness` (the step pods' init image, set as the chart's `harness.image` by
both deploys) ∥ `test` ∥ `frontend-lint` ∥ `build-sdk` ∥ `build-cli` → `publish-images` (tag `next`)
∥ `publish-sdk` (npm `--tag next`) ∥ `publish-cli` → `deploy-beta` (`helmDeployOverSsh` or `helmDeployInCluster`, selected by `target.beta.kind`; v2 beta in
`apps-beta`, values `values-beta.yaml` + `values-v2-beta.yaml`) → `beta-test` → when
`deployTo: "prod"`: `deploy-prod` (values `values-v2-prod.yaml`) → `promote` (`latest`, npm
dist-tag; prod's `/download` serves the promoted `pl`). Hosts, namespaces, registries, auth and
telemetry are the pipeline's config on the instance (`pl config get "Olve.Pipelines v2"`;
[docs/design/prod-config.example.yaml](docs/design/prod-config.example.yaml)). The deploy poll (no public webhook on
v2) reconciles `.pipelines/` commits into versions and starts a run per push that goes beta, then
prod (no manual promotion; block the `deploy-prod` gate only in special circumstances). `deploy-prod` restarts the pod running the run,
and the new pod reattaches to the Job. The chart is `helm upgrade`d imperatively — **helm values
are NOT GitOps-reconciled; only the pipeline definition is.** Skills: `/deploy`, `/setup-pipeline`.

### Two-tier model

`pl bootstrap` lays down only a single minimal **root (prod)** controller; everything else is a
Layer-1 pipeline the root self-deploys — a **beta** instance, and a separate **Olve.Homelab**
pipeline that owns shared infra (Ingress/DNS/cloudflared). The controller reaches the cluster API
with its own ServiceAccount token (`Kubernetes:AuthMode=InCluster`), and users sign in through the
bundled Dex (GitHub), served under `/dex` on the instance's host.

## Cold-boot install (`pl bootstrap`)

`pl bootstrap` is an idempotent cold install of the controller + its private MinIO via kubectl/helm
shell-out (the operator's kubeconfig): preflight + prod guard, namespace,
generate-if-absent MinIO creds Secret (the cluster Secret is the source of truth, never rotated on
re-run), chart from a GitHub tarball (`--ref`) or `--chart`, `helm upgrade` of the **minimal
profile** (`src/deploy/helm/values-minimal.yaml`: Ingress off, `Auth__Disable=true`, no OIDC/OTel secrets,
ServiceAccount/RBAC on), in-cluster `mc` bucket create, readiness waits.

`pl teardown` reverses it (helm uninstall + delete the creds Secret; the MinIO data PVC is retained
unless `--purge-data`; the namespace is never deleted).

See [docs/operations/environment-setup.md](docs/operations/environment-setup.md) and the design
specs under `docs/superpowers/specs/` (`pl-cli-self-installation`, `self-bootstrap-architecture`).

## `pl` CLI distribution

The `pl` binaries are built and served by this repo's own pipeline — no GitHub Releases.

**v2 (this branch):** the `build-cli` step of `.pipelines/pipeline.ts` (image `oven/bun`) checks
`src/cli` (prettier, tsc, `bun test`) and compiles `pl` with `bun build --compile` for `bun-linux-x64`
and `bun-windows-x64`, with the run's version stamp defined in (`pl --version`). `publish-cli`
re-emits both as **output files** (`pl-linux-x64`, `pl-win-x64.exe`; output
model `CliRelease`). No storage credentials in the step pod: the files travel in the normal result
upload into `runs/<p>/<run>/publish-cli/<attempt>/files/`. The app serves the latest successful
`publish-cli` attempt of the pipeline named by `Distribution__CliPipeline` (`values-v2-prod.yaml`:
"Olve.Pipelines v2"; unset → 404) anonymously at `GET|HEAD /download/{asset}`
(`Distribution/CliDownloadEndpoints.cs`, `CliReleaseQuery`; allow-listed names, streamed from
Garage, never buffered; anything else under `/download/` is a 404). Retention pins that run
(`RetentionInputs.PinnedRuns`), so the served `pl` survives retention.
`GET /install.sh` / `/install.ps1` serve the repo's scripts with the default instance rewritten
to this one (`Links:BaseUrl`, else the request origin).
