# `pl` CLI + self-installation — design spec

**Date:** 2026-06-15 (revised 2026-06-16)
**Status:** Design — decisions locked, scoped by priority, ready for a P0 plan
**Repo:** OliverVea/Olve.Pipelines

## Goal

Ship a `pl` CLI that installs Olve.Pipelines and its private dependencies onto a
bare Kubernetes cluster with one idempotent `pl install`, can cleanly remove it
with `pl uninstall`, and otherwise exposes **everything the API can do** as CLI
operations. The running controller continues to reconcile homelab apps via the
existing poll loop — **replacing ArgoCD**. New homelab bootstrap becomes
"install k3s → `pl install`".

This makes concrete the direction set in
[2026-06-14-self-bootstrap-architecture-design.md](2026-06-14-self-bootstrap-architecture-design.md)
(read it first — the two-layer model, bundled-private-deps decision, and the
2026-06-14 outage rationale live there and are not repeated here). It lifts the
in-repo GitOps reconcile model
([2026-06-14-in-repo-pipeline-config-design.md](2026-06-14-in-repo-pipeline-config-design.md))
up a level: from reconciling *pipeline config* to reconciling *the platform*.

## Priorities (2026-06-16)

The CLI is built in priority order; each layer is independently useful.

- **P0 — `pl install`** (cold-start installation). The most important piece. A bare
  cluster → a running, self-deploying controller on its private deps. Idempotent ⇒
  doubles as the disaster-recovery runbook.
- **P1 — `pl uninstall`**. Clean teardown of everything `pl install` laid down
  (Layer 0), with explicit handling of the precious data volume.
- **P2 — CLI operations over the app.** `pl` can do **anything the API can do** —
  register/rename/delete pipelines, manage steps/secrets/triggers, trigger
  production, re-promote an existing artifact, block/allow promotions, inspect
  jobs, etc. Thin CLI over the existing generated client (the API is the contract).
- **P3 (maybe) — runtime management.** `pl server start|stop|restart`-style
  lifecycle. **Deliberately out of the spec for now** — unclear it's worth it given
  reboots self-heal via ordered readiness gates (see Layer 0) and upgrades go
  through the self-deploy pipeline. Revisit only if a concrete need appears.

## Locked decisions (2026-06-16)

- **D1 — packaging: one binary.** A single AOT-compiled native binary. `pl install`,
  `pl uninstall`, and the P2 operations are subcommands; `pl serve` is the
  controller entrypoint that runs in-cluster. One Dockerfile, one version, one
  shared core assembly — the installer and the thing installed are provably the
  same build.
- **D2 — object store: private MinIO (DONE).** Resolved and **already shipped &
  proven in prod** (2026-06-16): a dedicated single-replica in-cluster MinIO with
  static root creds, no OIDC/STS, reusing the existing `AmazonS3` SDK paths. See
  [2026-06-15-dedicated-minio-storage-design.md](2026-06-15-dedicated-minio-storage-design.md)
  and `docs/operations/environment-setup.md`. `pl install` automates what that
  runbook does by hand.
- **D3 — Layer 1 reconcile: deploy-on-event for v1.** Keep the current poll-based
  deploy-on-event model (already built and running). Drift-detect + prune (true
  GitOps convergence, the full ArgoCD replacement) is a later phase.
- **Upgrades stay self-managed.** The controller keeps upgrading itself via the
  existing self-deploy pipeline (push to `main` → self-deploy). `pl` owns cold
  install + uninstall, not the running controller's upgrade lifecycle. *(Accepts a
  known tension with the "escape hatch is sacred" rule — `pl install` is the cold
  recovery path, so the self-deploy convenience is retained without losing the
  out-of-band repair route.)*

## Carried-forward decisions (from the target doc — not re-litigated)

- **Two layers.** Layer 0 (deps + controller) is owned out-of-band by `pl`, applied
  imperatively by `pl install`. Layer 1 (apps/workloads) is reconciled by the
  running controller via the existing poll loop.
- **Bundled private deps.** The controller's deps are ClusterIP-only, static-
  credentialed, no Ingress/OIDC — so the MinIO→Authentik→DNS→cloudflared failure
  chain that caused the outage cannot recur. Auth (human SSO) stays external by
  design. Kubernetes is the given. *(Note: the shipped MinIO is open to the cluster,
  no NetworkPolicy — a deliberate homelab call, "if they're in the cluster it's game
  over anyway". `pl install` follows the same posture unless revisited.)*
- **Escape hatch is sacred.** `pl` (out-of-band) is always able to install/repair a
  cluster without a running controller. `pl install` being idempotent is what makes
  this true.
- **One seed secret** generated at install lives outside the system permanently.

## Prerequisite — persistence hardening (DONE)

The gating must-fix before the controller is system-of-record: distinguish "load
failed" (transient/auth/parse) from "store genuinely empty (first run)"; on load
failure fail startup hard + retry (self-healing crashloop), never persist empty;
gate readiness on a successful snapshot load so the API never serves `[]` before
data is loaded.

**Shipped 2026-06-15** (`1cd6a65`, `19b1b3f`): `ISnapshotStore` seam,
FirstRun/Corrupt/LoadFailed classification, `_loaded` write-gate, crashloop on
failure, `/api/ready` readiness gate, `Storage:Mode` (Persistent/Ephemeral). See
[2026-06-15-persistence-hardening-design.md](2026-06-15-persistence-hardening-design.md).

## P0 — what `pl install` lays down (Layer 0)

Idempotent; re-running converges, never duplicates. Mirrors
`docs/operations/environment-setup.md`, automated:

1. **Namespace(s)** for the controller + its private deps.
2. **Private object store** — the dedicated MinIO (D2), Deployment/Service/PVC, with
   the bucket created (the one manual step in the runbook today).
3. **Static credentials** as k8s Secrets — the MinIO root creds and the seed secret.
4. **The controller** Deployment + ClusterIP Service, with **init containers /
   readiness gates** that block the controller until the store is healthy — encoding
   start-ordering declaratively so **reboots self-heal** without any `pl` runtime
   action (this is why P3 runtime management may be unnecessary).
5. **Ingress/route for the controller API** only (human + runner access), reusing the
   existing external auth.

**Credential handling (locked 2026-06-16): save it to the cluster — generate-if-
absent, no operator custody.** `pl install` writes the MinIO root creds to the k8s
Secret (`olve-pipelines-minio`), and that Secret **is** the source of truth (stored
in etcd) — the MinIO container and the controller both read it; nothing outside the
cluster consumes it. So there is **no "save this or else" step**: the operator isn't
required to store anything anywhere.

**Generate-if-absent (idempotency-critical):** on first install the Secret is absent
⇒ generate fresh random creds and create it. On any re-run (the idempotent / DR
convergence path) the Secret already exists ⇒ **leave it untouched**. Regenerating on
every run would rotate the password out from under a running MinIO and lock the
controller out of existing data. `pl` may echo creds only when it just created them.

Rationale (revised — the original "generate & print once, save it safely" had no
real consumer): the creds are **access credentials, not an encryption key** — MinIO
objects (snapshots + bundles) are stored plain. Recovery:
- **Lost creds** → `pl` regenerates the Secret + restarts MinIO and the controller.
  No data loss (the data isn't encrypted with them).
- **Lost data** → restore the **MinIO data PVC** — *that* is the precious thing to
  back up, not the creds.

The abstract "one seed secret lives outside the system" escape-hatch idea from the
architecture doc does **not** map to a concrete consumer here: `pl` reaches the
cluster via the operator's kubeconfig, not a bootstrap secret. Dropped unless a real
need appears. A `--seed <file>` override (operator supplies their own creds, e.g.
from a password manager) can be added later if wanted. `pl` assumes k3s is already
present (k3s is the boundary).

## P1 — `pl uninstall`

Removes everything `pl install` created (reverse dependency order, controller first).
Must handle the **precious MinIO data PVC explicitly** — it holds snapshots +
bundles, so deletion is opt-in:

- Default: remove workloads (controller, MinIO Deployment/Service, Ingress,
  Secrets, namespaces) but **retain the data PVC** unless `--purge-data`.
- `--purge-data`: also delete the PVC (full wipe — the inverse of a clean install).
- Idempotent: re-running on a partial/already-gone install converges.

## P2 — CLI operations over the app (API parity)

`pl` should expose **everything the API can do**. The API/UI is the contract; `pl`
is a thin client over the **existing generated client** (Refit C#, already
`returnIApiResponse: true`), so parity is mostly wiring, not new domain logic.
Covers the current surface (pipelines: register/rename/delete; production/processing
steps + config; secrets; triggers; jobs; trigger production) plus two **new
capabilities that must land in API + UI first** (they are product features, not
`pl`-specific):

- **Re-promotion** — initiate a fresh promotion of an *existing* artifact bundle
  through processing (re-run a deploy of an already-built artifact without rebuilding).
  Needs an API endpoint + UI action; `pl` then wraps it.
- **Promotion gating / blocking** — block (and later release) promotion between
  processing steps (a manual hold/approval gate). Needs API + UI support; `pl` wraps
  it.

These two are tracked as their own work items; P2 `pl` commands for them follow once
the API exists.

## P3 (maybe) — runtime management

Out of scope for now (see Priorities). If revisited: dependency-ordered
`start|stop|restart` performed out-of-band by `pl`. Currently judged unnecessary
because Layer 0 readiness gates make reboots self-heal and upgrades go through the
self-deploy pipeline.

## Layer 1 — what the running controller reconciles

Apps/workloads as bound pipelines via the existing poll-based reconcile loop
(deploy-on-event, D3). The controller becomes the homelab's reconcile layer
(ArgoCD's job). Drift-detect + prune is the later ArgoCD-replacement target, gated on
the now-shipped persistence hardening. Observability and other infra likely become
Layer 1 workloads too — out of v1 scope, called out so schema/labels don't preclude
it.

## Out of scope (keep extensible)

- P3 runtime management (above).
- Layer 1 drift-detect + prune (deploy-on-event stays the v1 reconcile model).
- Bundling observability / non-controller infra as Layer 1.
- Multi-cluster / HA controller (single-replica homelab target).
- Migrating *existing* data into the private store — `pl install` targets a
  fresh/private store; any data move is a separate one-off (and prod already cut over).
- `pl` installing k3s itself (k3s assumed present).

## Open questions

- **Code reuse for P2** — generate `pl`'s command surface from the OpenAPI spec /
  generated client, or hand-write commands? (Generation keeps parity automatic as the
  API grows.)
- **Re-promotion & gating data model** — where the artifact reference + gate state
  live, and how the UI surfaces them (separate design before building).
