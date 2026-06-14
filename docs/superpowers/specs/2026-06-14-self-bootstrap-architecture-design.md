# Self-bootstrapping infra: `pl install` + bundled private deps — design target

**Date:** 2026-06-14
**Status:** Target / direction — not yet scoped to a plan
**Repo:** OliverVea/Olve.Pipelines

## Goal

Given only a Kubernetes cluster, Olve.Pipelines installs itself and its
dependencies with a single `pl install`, then becomes the reconcile layer for all
homelab infra — **replacing ArgoCD**. In the user's words: "I'd love if all my
infra was defined in Olve.Pipelines (except for the dependencies — s3, openbao,
kubernetes), and Olve.Pipelines can install itself through a `pl install` command."

This lifts the in-repo GitOps reconcile model (see
`2026-06-14-in-repo-pipeline-config-design.md`) up a level: from reconciling
*pipeline config* to reconciling *the platform itself*.

## Two-layer model

- **Layer 0 (bootstrap)** — embedded in the `pl` binary, applied imperatively by
  `pl install`. The only thing living outside the self-managed world. Kubernetes is
  the given substrate; `pl` lays down everything else.
- **Layer 1 (workloads)** — apps (and likely observability) defined in / reconciled
  by the running controller, via the existing poll-based reconcile loop.

## Bundled private dependencies (not shared)

Decision: the service's deps are **private to the controller** — ClusterIP-only,
NetworkPolicy-locked, static credentials injected at install, no Ingress. Not
exposed to other apps. "I'd actually be kind of okay if they were just bundled
together and not accessible for other applications."

**Why this is the important decision (concrete, from the 2026-06-14 outage):** the
shared homelab MinIO authenticated via STS/OIDC, so its storage chained
MinIO → Authentik → DNS → cloudflared. On a cold restart MinIO came up before
Authentik/DNS were reachable, its one-shot OpenID init timed out, the STS role went
undefined, and the controller could neither read nor write its S3 state — it served
an empty pipeline list. A **private MinIO with static creds needs none of that
chain**, so the entire failure class disappears. Bundling buys the resilience, not
just the simplicity.

Right-sized to *this* service's actual architecture (JSON-in-S3, no DB, AOT, no EF
Core):

- **Object store (S3) — the one real heavyweight dep.** State is JSON in MinIO.
  Bundle a private single-replica MinIO with static root creds and no OIDC/STS
  (keeps the existing `AmazonS3` SDK paths) — OR drop to an embedded blob store /
  PVC. *Open: private MinIO vs embedded store.*
- **Secrets — already aligned, nothing to bundle.** Pipeline config keeps secret
  *values* in k8s Secrets (per the in-repo config spec), never a secrets engine for
  the service's own config. So "openbao" is not actually a dep of the service —
  only keep a secrets engine if *runners* need one for deploy creds.
- **Auth — the deliberate exception.** Human SSO (Authentik OIDC, see
  `project_auth`) is inherently shared across apps; keep it external. Not a storage
  dep, not bundled.
- **Kubernetes** — substrate + runner execution; the "given," never bundled.

## `pl` CLI surface

- `pl install` — idempotent cold bootstrap from a bare cluster. Because it's
  idempotent it **doubles as the disaster-recovery runbook**: point `pl` at a fresh
  cluster → Layer 0 comes up → controller reconciles Layer 1. The homelab bootstrap
  goal becomes "install k3s → `pl install`" (replacing ArgoCD + ApplicationSet).
- `pl server start | stop | restart` — dependency-ordered lifecycle. `start` brings
  deps to healthy *before* the controller (so it never boots into missing storage
  and serves empty). `restart` bakes in the safe sequence performed by hand during
  the outage: quiesce the writer → bounce → bring the controller back only after
  deps are confirmed healthy, so empty in-memory state can never overwrite the good
  store.

## Prerequisites (blockers, not afterthoughts)

1. **Persistence hardening — the footgun.** The persistence services currently catch
   a load failure, log "starting fresh," then save — which can overwrite good S3
   state with empty (only dodged during the outage because the *save* failed on the
   same STS error). Required: distinguish "load failed" (transient / auth / parse)
   from "store genuinely empty (first run)"; on failure, fail startup hard + retry
   (a self-healing crashloop) instead of persisting empty; gate readiness on a
   successful snapshot load so the API never serves `[]` before data is loaded.
   **This must land before the controller is system-of-record for anything
   important.**
2. **`pl install` ≠ reboot recovery.** Install is cold-start from nothing; reboots
   must self-heal via the manifests `pl` lays down (ordered readiness probes; init
   containers gating on dependency health). Don't re-run `pl install` per reboot.
3. **Escape hatch.** `pl` (out-of-band) owns the lifecycle of Layer 0 + the
   controller itself; the running controller owns only Layer 1. Never make the
   running system the only thing that can repair the running system.
4. **The seed / root of trust.** One static secret generated at install lives
   outside the system permanently (you can't self-manage the credential that unlocks
   everything). With private static-cred deps this is one secret, not an OIDC realm.

## Open questions

- Private MinIO vs embedded object store for the S3 state (leaning private MinIO,
  since persistence is already on the `AmazonS3` SDK).
- Layer 1 reconcile: continuous drift-detect + prune (extends the existing poll-based
  reconcile loop) vs deploy-on-event. The former implies a rock-solid store — gated
  on prerequisite #1.
