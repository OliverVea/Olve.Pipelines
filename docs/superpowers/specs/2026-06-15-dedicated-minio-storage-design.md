# Design — dedicated in-cluster MinIO with static credentials

**Status:** Draft — for review before implementation.
**Date:** 2026-06-15

## Goal

Replace the shared `minio.ovea.pro` (reached via STS → Authentik) with a **dedicated MinIO
instance deployed alongside the controller**, accessed with **static credentials** from a k8s
Secret. This removes the auth chain (STS/Authentik/OpenBao-for-storage) from the
startup-critical path — the cause of the 2026-06-14 outage — while keeping the existing S3
code (`S3SnapshotStore`, `S3BundleStore`, the per-job `mc` bundle plumbing) essentially
unchanged.

Decisions locked with the user:
- **Dedicated MinIO**, separate from `minio.ovea.pro`. One instance per environment.
- **Static creds** in a k8s Secret — no STS, no Authentik, no OpenBao for storage.
- **Deployed with the pipeline** — part of the in-repo helm chart that `deploy.sh` /
  `deploy-beta.sh` already `helm upgrade --install`, so a push ships MinIO too.
- **Open to the cluster** — no NetworkPolicy. ("If they're in the cluster it's game over
  anyway.") Single-user homelab; intra-cluster segmentation isn't worth it.
- Single replica (single-node k3s; HA is moot).

## Why this is mostly config, not code

`StorageConfiguration.ConfigureStorage` already prefers static creds:

```csharp
if (accessKey is not null && secretKey is not null)
    credentialsProvider = new DirectCredentialsProvider<S3Credentials>(new S3Credentials(accessKey, secretKey));
else if (authUrl is not null && clientId is not null && ...)   // STS path — becomes dead config
```

And `KubernetesJobExecutor.CreateS3CredentialsSecretAsync` builds the `MC_HOST_s3` string and
already handles the no-session-token case:

```csharp
var creds = ...;  // now static -> no SessionToken
var hostValue = creds.SessionToken is not null ? "ACCESS:SECRET:TOKEN" : "ACCESS:SECRET";
```

So setting `Storage:AccessKey`/`Storage:SecretKey` + repointing `Storage:Endpoint` makes both
the controller AND the runner Jobs use the new MinIO. **No app source change is expected** for
the credential switch — verify, don't assume.

## What changes

### 1. Helm — add MinIO to the chart

New templates under `helm/templates/` (gated on `minio.enabled`, default true):

- **`minio-deployment.yaml`** — `minio/minio` (pin a version), single replica, `server /data
  --console-address :9090`. Env from the creds Secret (`MINIO_ROOT_USER` /
  `MINIO_ROOT_PASSWORD`). `MINIO_DEFAULT_BUCKETS: olve-pipelines` to auto-create the bucket on
  first boot (otherwise an init step running `mc mb` — see Bucket bootstrap).
- **`minio-service.yaml`** — ClusterIP, port 9000 (S3 API). Name: `{{ .Release.Name }}-minio`.
- **`minio-pvc.yaml`** — `local-path` PVC for `/data` (e.g. 10Gi). Single-node, RWO is fine.
  This is the only thing that needs backing up (host dir under k3s local-path storage).
- **`minio-secret.yaml`** — static `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD`. Created from
  helm values that come from the existing helm secret mechanism (NOT committed to the repo).
  See Secrets below.

No console ingress (cluster-internal only). No NetworkPolicy.

### 2. Helm config — switch the controller to static creds + in-cluster endpoint

Per-environment `config:` / `secrets:`:
- `Storage__Endpoint` → `http://olve-pipelines-minio.<ns>:9000` (was the `minio*.ovea.pro` host).
  FQDN form so the cross-namespace runner Jobs resolve it too.
- `Storage__Bucket` unchanged per env (`olve-pipelines` prod, `olve-pipelines-beta` beta).
- **Add** `Storage__AccessKey` / `Storage__SecretKey` via `secretKeyRef` to the new
  `olve-pipelines-minio` Secret (keys `root-user` / `root-password`).
- **Do NOT remove** `Storage__AuthUrl` / `Storage__ClientId` / `Storage__ClientSecret`.
  **Correction to the original plan:** `KubernetesConfiguration` *reuses* these for OpenBao
  auth to reach the cluster API. Removing them breaks Job launching. With static creds present,
  `ConfigureStorage` takes the `DirectCredentialsProvider` branch first, so the STS config is
  simply inert for storage while still serving Kubernetes/OpenBao. (Decoupling K8s auth from
  the storage keys is a separate, later cleanup.)

Roll out **beta-first by values**: `minio.enabled` defaults `false` in `values.yaml` (prod
untouched) and is set `true` in `values-beta.yaml` along with the endpoint/creds switch. So one
push deploys the new backend to beta only; prod stays on `minio.ovea.pro` until a later push
flips `values.yaml`.

Endpoint is **cluster-internal HTTP** (no TLS, no self-signed cert) → `SkipCertValidation` is
irrelevant; the AWS SDK `ServiceURL=http://...` + `ForcePathStyle=true` already handles plain
HTTP. The runner Jobs reach it via the same FQDN (cross-namespace DNS works; network is open).

### 3. Bucket bootstrap

A fresh MinIO has no bucket, and the official `minio/minio` image does **not** honor
`MINIO_DEFAULT_BUCKETS` (that's a Bitnami-image feature). The controller/Jobs don't create the
bucket either. So for now the bucket is a **manual `mc mb` step** in the runbook (run once after
MinIO is up). The controller crashloops until it exists — expected and self-healing. A helm
post-install hook Job can automate this later; kept manual for the interim per the runbook
approach.

### 4. Secrets + bucket → a markdown setup runbook (interim)

The friendly, self-service bootstrap belongs to the **`pl` CLI changeset**
(`2026-06-15-pl-cli-self-installation-design.md`). For now we only need a fresh environment to
be *reproducible by an engineer* — not automated. So instead of a robust script, capture it as
a **markdown runbook** (`docs/operations/environment-setup.md`) with copy-pasteable commands.
(Today the pieces are scattered across the README config table, the `setup-pipeline` skill, and
handoff/memory — this consolidates them.)

The runbook documents, per environment (`apps` / `apps-beta`):
1. **Namespaces** that must exist (`apps`, `apps-beta`, `olve-runners`, `olve-runners-beta`).
2. **The MinIO creds Secret** — the one thing helm can't pull from the repo. A single
   copy-pasteable, idempotent command:
   `kubectl create secret generic olve-pipelines-minio -n <ns> \
     --from-literal=MINIO_ROOT_USER=… --from-literal=MINIO_ROOT_PASSWORD=… \
     --dry-run=client -o yaml | kubectl apply -f -`
   Controller and MinIO share the same root creds (root = app creds for now; scope a dedicated
   MinIO user later, out of scope). Referenced by `secretKeyRef` from both the controller env
   and the MinIO Deployment — an *external* Secret, matching how `olve-pipelines-oidc` works.
3. **Bucket** — auto-created by `MINIO_DEFAULT_BUCKETS` on the MinIO container; the runbook
   notes the `mc mb --ignore-existing` fallback only if that proves unreliable.
4. **The deploy** — push the helm change; the normal deploy step ships MinIO + the repointed
   controller.

Everything except the Secret is in the helm chart and applied by the normal deploy. First-time
flow: create the Secret (step 2) → push the helm change → deploy helm-upgrades MinIO + the
repointed controller → MinIO boots, creates the bucket → controller connects FirstRun.

**Ordering is self-healing:** within the single `helm upgrade`, the controller and MinIO pods
come up together. The controller crashloops (by hardening design) until MinIO is ready and the
bucket exists, then connects — no manual sequencing inside the deploy needed, as long as the
Secret exists first.

> The `pl` changeset later subsumes this runbook: `pl` will provision the Secret, ensure the
> namespace, and bundle/launch the private MinIO as part of self-installation. The markdown is
> the throwaway manual stand-in until then.

## What stays the same

- `S3SnapshotStore`, `S3BundleStore` — unchanged.
- The persistence hardening (FirstRun/Corrupt/LoadFailed classification, `_loaded` gate,
  `/api/ready`, `Storage:Mode`) — unchanged; persistent mode still works because an
  `ISnapshotStore` is still registered (endpoint + static creds present).
- The `GetDocument.Insider` Ephemeral override (`19b1b3f`) — unchanged.
- Per-job S3 secret lifecycle (`olve-s3-{jobId}`, outlives cancelled watchers across
  self-deploy) — unchanged.

## Cutover (prod auto-deploys on push — sequence carefully)

Prod state currently lives in the shared `minio.ovea.pro`. The dedicated MinIO starts empty ⇒
controller comes up FirstRun.
- **Config self-heals** (`.pipelines/config.yaml` → GitOps reconcile rematerializes it).
- **Bundles + job history are lost.** **Decided: accept the reset** (history is disposable;
  in-flight bundles regenerate on re-trigger). No `mc mirror` migration.

Sequence:
1. **Beta first.** Land helm (MinIO templates + config switch) so beta deploys the dedicated
   MinIO in `apps-beta`. Verify: MinIO pod ready, bucket exists, `/api/ready` 200 (snapshots
   loaded FirstRun), a config mutation round-trips into the new MinIO, a full pipeline run
   produces+consumes a bundle.
2. **Prod.** Same change reaches prod via the deploy step. Because helm (MinIO + Secret + PVC)
   and the config switch are in one chart version, they apply atomically in the
   `helm upgrade`. Confirm FirstRun, reconcile rematerializes config, a deploy round-trips.
3. Decommission the app's access to `minio.ovea.pro` (leave the shared instance alone).

**Bootstrapping caveat:** the very deploy that introduces the dedicated MinIO is itself run by
a pipeline whose Jobs use the *old* S3 endpoint for that run's bundle. That's fine — the new
endpoint only takes effect for the controller + subsequent runs after the helm upgrade
restarts the pod. Verify the in-flight self-deploy run completes against the old endpoint
before the pod swaps.

## Testing

- App: confirm no source change needed; if any, unit-test the static-cred path in
  `ConfigureStorage` and the `MC_HOST_s3` no-token branch (likely already covered).
- Integration tests already run against a MinIO (static creds) — should be unaffected; verify
  endpoint/config wiring matches.
- Beta is the real integration test for the helm/MinIO deployment.

## Risks / watch-items

- **Bucket bootstrap** must be reliable (MINIO_DEFAULT_BUCKETS or init `mc mb`), else FirstRun
  save fails and the pod crashloops (by hardening design).
- **MinIO data PVC is the new precious volume** — back it up (snapshots + bundles live here).
  Single-node local-path = a host dir under `/var/lib/rancher/k3s/storage`.
- **Secret provisioning** — the static creds Secret must exist before first controller start in
  each namespace, or `ISnapshotStore` registration/startup fails.
- **Cross-namespace endpoint** — Jobs in `olve-runners` must resolve
  `olve-pipelines-minio.apps:9000`. Open network + cluster DNS makes this work; confirm.
- **Two MinIOs now** (shared + dedicated) — slightly more to run, but isolation is the point.

## Decisions

- **MinIO in the existing helm chart** (not separate manifests) — deploy.sh already
  helm-upgrades it; one atomic release.
- **Root creds = app creds** for now; scope a dedicated MinIO user later.
- **Accept FirstRun reset** — no bundle/job-history migration. *(confirmed by user)*
- **Interim bootstrap = markdown runbook** (`docs/operations/environment-setup.md`), not a
  script; the `pl` changeset automates it later. *(confirmed by user)*
- **No NetworkPolicy** — MinIO open to the cluster. *(confirmed by user)*

## Open questions (for review)

1. **PVC size** for MinIO `/data` (bundles accumulate) — 10Gi to start? A bundle-retention/prune
   policy is a later concern (note it, don't build now).
