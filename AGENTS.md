# AGENTS.md

Agent-facing context for **building, structuring, deploying, and cold-boot-installing**
Olve.Pipelines. For design patterns and conventions see [CLAUDE.md](CLAUDE.md); for the
product/usage reference see [README.md](README.md) and the running instance's `/docs` (source
under [docs/setup](docs/setup)). Operations runbooks live in [docs/operations](docs/operations).

## How it's built

- .NET 10, Native-AOT published. Controller: `src/Olve.Pipelines`. Operator CLI:
  `src/Olve.Pipelines.Cli` (binary `pl`, a separate AOT project that shares ~no runtime code).
- `dotnet build` / `dotnet test` (unit) / `dotnet test -p:RunIntegrationTests=true` (all).
- In the pipeline, the container is built with **Kaniko** (`.pipelines/scripts/build.sh`) while
  the test suite runs in parallel (`scripts/test.sh`). The build stamps a timestamp `VERSION`
  (`YYYYMMDD-HHMMSS`) into `version.txt` in the bundle; the deploy and publish-cli steps read it
  back so everything in a run shares one version. (It is a timestamp, not a semver — anything that
  stamps a .NET `AssemblyVersion`/`FileVersion` from it will fail; use `InformationalVersion`.)

## Structure

CLAUDE.md is the detailed map (service-layer architecture: EntityStore / AttachmentStore / event
hubs / domain services, plus conventions). Top level: `src/Olve.Pipelines` (controller),
`src/Olve.Pipelines.Cli` (`pl`), `helm/` (chart + per-env values), `.pipelines/` (this repo's own
deploy config), `frontend/` (Lit SPA), `docs/`.

## How it's deployed (self-deploy)

Olve.Pipelines deploys itself. `.pipelines/config.yaml` is the source of truth; a reconcile loop
polls `main` and a GitHub push webhook triggers a run. Flow: `build-and-package` + `code-test`
(production, parallel) → `deploy-beta` → `deploy` (prod, gated on beta) → `publish-cli`. The deploy
steps `helm upgrade` the chart (carried in the bundle) imperatively — **helm values are NOT
GitOps-reconciled; only `.pipelines/config.yaml` is.** Pushing `main` self-deploys.

### Two-tier model

`pl bootstrap` lays down only a single minimal **root (prod)** controller; everything else is a
Layer-1 pipeline the root self-deploys — a **beta** instance, and a separate **Olve.Homelab**
pipeline that owns shared infra (Ingress/DNS/cloudflared + Authentik). The controller reaches the
cluster API via its own ServiceAccount token (`Kubernetes:AuthMode=InCluster`); prod keeps
`AuthMode=OpenBao` (explicit in `helm/values.yaml`).

## Cold-boot install (`pl bootstrap`)

`pl bootstrap` is an idempotent cold install of the controller + its private MinIO via kubectl/helm
shell-out (operator's kubeconfig, not OpenBao): preflight + prod guard, namespace,
generate-if-absent MinIO creds Secret (the cluster Secret is the source of truth, never rotated on
re-run), chart from a GitHub tarball (`--ref`) or `--chart`, `helm upgrade` of the **minimal
profile** (`helm/values-minimal.yaml`: Ingress off, `Auth__Disable=true`, no OIDC/OTel secrets,
ServiceAccount/RBAC on), in-cluster `mc` bucket create, readiness waits.

`pl teardown` reverses it (helm uninstall + delete the creds Secret; the MinIO data PVC is retained
unless `--purge-data`; the namespace is never deleted).

See [docs/operations/environment-setup.md](docs/operations/environment-setup.md) and the design
specs under `docs/superpowers/specs/` (`pl-cli-self-installation`, `self-bootstrap-architecture`).

## `pl` CLI distribution

The `pl` binaries are built and served by this repo's own pipeline — no GitHub Releases. The
`publish-cli` processing step (last; `.pipelines/scripts/publish-cli.sh`) builds `linux-x64`
(Native AOT) + `win-x64` (self-contained single-file — AOT can't cross-compile to Windows from a
Linux runner) and `mc cp`s both to the instance's MinIO under `cli/{latest,<version>}/`. The app
serves them anonymously from `GET /download/{asset}` (`Distribution/CliDownloadEndpoints.cs`,
allow-listed asset names, streamed from S3, never buffered). `install.sh` / `install.ps1` fetch
from there. **Operator prereq:** set `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` on the pipeline secret
to the app's MinIO root creds (k8s secret `olve-pipelines-minio`, keys `root-user`/`root-password`).
