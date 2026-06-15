# `pl` CLI + self-installation — design spec

**Date:** 2026-06-15
**Status:** Design draft → ready for review
**Repo:** OliverVea/Olve.Pipelines

## Goal

Ship a `pl` CLI that installs Olve.Pipelines and its private dependencies onto a
bare Kubernetes cluster with one idempotent `pl install`, then lets the running
controller reconcile the rest of homelab infra — **replacing ArgoCD**. New homelab
bootstrap becomes "install k3s → `pl install`".

This makes concrete the direction set in
[2026-06-14-self-bootstrap-architecture-design.md](2026-06-14-self-bootstrap-architecture-design.md)
(read it first — the two-layer model, bundled-private-deps decision, and the
2026-06-14 outage rationale live there and are not repeated here). This spec scopes
**what `pl` is, what `pl install` lays down, and the ordering/lifecycle/recovery
contracts** toward a phased plan.

It lifts the in-repo GitOps reconcile model
([2026-06-14-in-repo-pipeline-config-design.md](2026-06-14-in-repo-pipeline-config-design.md))
up a level: from reconciling *pipeline config* to reconciling *the platform*.

## Carried-forward decisions (from the target doc — not re-litigated)

- **Two layers.** Layer 0 (deps + controller) is owned out-of-band by `pl`,
  applied imperatively by `pl install`. Layer 1 (apps/workloads) is reconciled by
  the running controller via the existing poll loop.
- **Bundled private deps.** The controller's deps are ClusterIP-only,
  NetworkPolicy-locked, static-credentialed, no Ingress/OIDC — so the
  MinIO→Authentik→DNS→cloudflared failure chain that caused the outage cannot
  recur. Auth (human SSO) stays external by design. Kubernetes is the given.
- **Escape hatch is sacred.** The running controller never owns the lifecycle of
  Layer 0 or itself — `pl` does. Never make the running system the only thing that
  can repair the running system.
- **One seed secret** generated at install lives outside the system permanently.

## Prerequisite (gating, lands first) — persistence hardening

The single must-fix before the controller is system-of-record for anything,
escalated from the target doc's prerequisite #1:

- The persistence layer currently catches a load failure, logs "starting fresh,"
  then **saves** — which can overwrite good S3 state with empty (only dodged in the
  outage because the save *also* failed).
- Required: **distinguish "load failed" (transient/auth/parse) from "store
  genuinely empty (first run)."** On load failure → **fail startup hard + retry**
  (self-healing crashloop), never persist empty. **Gate readiness** on a successful
  snapshot load so the API never serves `[]` before data is loaded.
- This is a controller-side change (not `pl`), but it is **phase 1** of this work —
  `pl install`/`restart` resilience is meaningless on a store that can self-erase.

## `pl` — what it is

- A **single AOT-compiled native binary** (same toolchain as the service; the repo
  is already AOT). Distributed standalone; no runtime on the operator's machine.
- **Recommended:** `pl` and the controller share one repository and core assembly;
  `pl` is the CLI/installer face, the service is the long-running face. *(Decision
  D1 below: same binary w/ subcommands vs two binaries from one solution.)*
- Talks to the cluster via the Kubernetes API using the operator's existing
  kubeconfig (the same `KubernetesClient` the service already uses). No in-cluster
  agent required for install.
- All output via `Olve.Results` → human-readable problems; non-zero exit on failure.

## `pl` command surface (v1)

- **`pl install`** — idempotent cold bootstrap from a bare cluster. Applies Layer 0:
  namespace(s), the private dep(s) (object store + creds), the controller
  Deployment/Service, NetworkPolicies, and the seed secret. Idempotent ⇒ **doubles
  as the disaster-recovery runbook** (point at a fresh cluster → Layer 0 up →
  controller reconciles Layer 1). Re-running converges, never duplicates.
- **`pl server start | stop | restart`** — dependency-ordered lifecycle.
  - `start`: bring deps to **healthy** before the controller, so it never boots into
    missing storage and serves empty.
  - `restart`: the safe sequence performed by hand during the outage — quiesce the
    writer → bounce → bring the controller back **only after** deps are confirmed
    healthy, so empty in-memory state can't overwrite the good store.
  - `stop`: dependency-reverse, controller first.
- **`pl status`** — Layer 0 health (deps reachable, controller ready, last snapshot
  load OK). The operator's out-of-band view that does not depend on the controller
  being healthy.

*(`pl install` ≠ reboot recovery: reboots self-heal via the manifests `pl` lays
down — ordered readiness probes + init containers gating on dependency health — not
by re-running install.)*

## What `pl install` lays down (Layer 0)

1. **Namespace(s)** for the controller + its private deps.
2. **Private object store** for the JSON-in-S3 state — *Decision D2: private
   single-replica MinIO (static root creds, no OIDC/STS, keeps the existing
   `AmazonS3` SDK paths) vs an embedded blob store / PVC.* Leaning private MinIO.
3. **Static credentials** as k8s Secrets (the store creds; the seed secret).
4. **The controller** Deployment + ClusterIP Service, with **init containers /
   readiness gates** that block the controller until the store is healthy
   (encodes `start` ordering declaratively so reboots self-heal).
5. **NetworkPolicies** locking the deps to controller-only access; no Ingress on
   deps.
6. **Ingress/route for the controller API** only (human + runner access), reusing
   the existing external auth.

## Layer 1 — what the running controller reconciles

Apps/workloads as bound pipelines via the existing poll-based reconcile loop. The
controller becomes the homelab's reconcile layer (ArgoCD's job).

- *Decision D3: continuous drift-detect + prune (true GitOps convergence) vs
  deploy-on-event (current model).* Drift+prune is the ArgoCD-replacement target
  but implies a rock-solid store — **gated on the persistence-hardening
  prerequisite**. Recommend: ship deploy-on-event first (already built), add
  drift+prune as a later phase once persistence is hard.
- Observability (and other infra) likely become Layer 1 workloads too — out of v1
  scope, called out so the schema/labels don't preclude it.

## Scope

In v1:

- Persistence hardening (gating phase 1).
- `pl` binary + `install`, `server start|stop|restart`, `status`.
- Layer 0 manifests with ordered readiness so reboots self-heal.
- Private object store with static creds (pending D2).
- Idempotent install doubling as DR.

Out of v1 (keep extensible):

- Layer 1 drift-detect + prune (deploy-on-event stays the v1 reconcile model).
- Bundling observability / non-controller infra as Layer 1.
- Multi-cluster / HA controller (single-replica homelab target).
- Migrating off the shared homelab MinIO for *existing* data — `pl install` targets
  a fresh/private store; any data move is a separate one-off.
- A secrets engine for the service's own config (none needed — values live in k8s
  Secrets already). Only revisit if *runners* need one for deploy creds.

## Decisions needed (your call)

- **D1 — `pl` packaging:** one binary with `pl serve` as the controller entrypoint
  *(simplest: single artifact, shared code, the installer and the thing installed
  are provably the same version)*, or two binaries from one solution?
- **D2 — object store:** private single-replica MinIO (keeps `AmazonS3` SDK, least
  code) vs embedded blob store / PVC (one fewer process, but new persistence path)?
- **D3 — Layer 1 reconcile:** confirm deploy-on-event for v1, drift+prune deferred
  behind persistence hardening?

## Open questions

- Seed-secret delivery: how the operator supplies/stores the one external root
  secret (file? prompt? `pl install --seed`?).
- Does `pl` need to install k3s itself, or assume k3s present (target doc says
  "install k3s → `pl install`", i.e. k3s is the boundary)? Assuming present.
- Upgrade path: is controller upgrade a `pl` concern (Layer 0) or self-managed via a
  bound pipeline (the controller deploying itself — which it already does today via
  the self-deploy pipeline)? Tension with the escape-hatch rule worth resolving.
