# Binding & Reconcile

[← Index](index.md) · [Subject Index](subjects.md)

How a pipeline is tied to your repo, how the reconcile loop keeps the live pipeline matching
your committed config, and how to read the status.

## The binding

A **binding** ties one pipeline to one repo + branch + config path. It records:

| Field | Meaning |
|---|---|
| `repo` | `owner/name` on GitHub |
| `branch` | branch to watch (default `main`) |
| `path` | config directory in the repo (default `.pipelines`) |
| `credentialsSecret` | the **key name** in the pipeline's k8s secret holding the repo read token (omit for a public repo) — a reference, never a raw token |
| `lastSyncedSha` / `lastDeployedSha` | the commit last reconciled / last deployed |
| `status` | last reconcile `result`, `lastSyncTime`, and `problems` |

Binding also **configures the deploy trigger** automatically — that's why you never author one
by hand. How that trigger fires (webhook, webhook-only, or poll) is the binding's
[deploy-trigger mode](#deploy-trigger-mode).

### Binding commands

`pl binding create` is the **only** way to create a pipeline — there is no unbound/draft
create. Every pipeline is bound to a repo from birth, so its shape always comes from git.

| `pl` command | Purpose |
|---|---|
| `pl binding create <repo> [--branch <b>] [--path <dir>] [--credentials-secret <key>] [--trigger <mode>]` | Create a pipeline already bound to a repo (rolls back the pipeline if the bind fails). The name is **not** an argument — it comes from `config.yaml`'s `name` on the first reconcile; the bind seeds a provisional name from the repo. |
| `pl binding get <pipelineId>` | Get the binding |
| `pl binding status <pipelineId>` | Reconcile result/problems + live secret set/unset |
| `pl binding set-trigger <pipelineId> <mode>` | Change the [deploy-trigger mode](#deploy-trigger-mode) |
| `pl binding set-credentials <pipelineId> [secretName]` | Set (or, with no name, clear) the repo-read credentials secret key |
| `pl binding reconcile <pipelineId>` | Apply the bound config now, off the poll schedule |

`--branch` defaults to `main`, `--path` to `.pipelines`, `--trigger` to `webhook`.

### Deploy-trigger mode

How a bound pipeline learns it should deploy. Mutable at any time with
`pl binding set-trigger <pipelineId> <mode>`, where `<mode>` is `webhook`, `webhook-only`, or
`poll`.

| Mode (`--trigger` / `set-trigger`) | Behavior |
|---|---|
| `webhook` (default) | Auto-registers a GitHub `push` hook that runs reconcile+deploy on each push; the poll still runs as a **15-min safety net** (catches a missed/failed delivery). |
| `webhook-only` | Webhook only — polling is suppressed once the hook is confirmed live. The explicit "opt out of polling" mode. |
| `poll` | No webhook; the 15-min poll is the sole trigger. |

Webhook modes need **`Webhooks:PublicBaseUrl`** configured server-side (so GitHub can reach the
receiver) and the binding's **`credentialsSecret`** token to carry **`admin:repo_hook`** (it both
fetches the repo and manages the hook). The inbound delivery is authenticated by an HMAC secret the
binding generates — there's no inbound secret to manage. If a hook can't be registered (no public
URL, no credentials, registration failure), **polling continues regardless of mode** so deploys
never silently stop. The webhook path runs the same config-before-build cycle as the poll.

> Pre-existing bindings (created before deploy-trigger modes) load as **`poll`** — they keep
> their current behavior and don't silently adopt webhooks. Switch them with
> `pl binding set-trigger <pipelineId> webhook`.

## The reconcile loop

A background poll (~15 min) asks GitHub for the branch head — and/or a push webhook drives the
same cycle on demand (see [deploy-trigger mode](#deploy-trigger-mode)). The fetch is **pull-based,
read-only, and cheap**:

1. **Branch-head check** — get the latest commit SHA for the branch.
2. **Conditional config fetch** — a cursor request (the head commit touching the config
   `path`) sent with an **ETag** (`If-None-Match`). If GitHub returns `304 Not Modified`,
   nothing under `.pipelines/` changed and the fetch costs nothing.
3. **Tree + blobs** — on a change, fetch the git tree at that commit and pull every blob under
   the config `path` into an in-memory file map (`config.yaml`, `steps/*.yaml`, `scripts/*.sh`).
4. **Compile + validate** — resolve `$ref`/`scriptFile`, deserialize, and run the
   [validation rules](config-reference.md#validation-rules-reconcile-rejects-on-any-of-these).
5. **Apply** — materialize production steps, processing steps, and triggers to match the file
   (additive + drain: things removed from the file are removed from the pipeline).
6. **Build** — run the build for the new commit.

Idle polls and code-only pushes are nearly free thanks to the ETag. A code-only push (nothing
under `.pipelines/` changed) still builds, but skips re-materializing the shape.

## config-before-build

The reconcile is **config-before-build**: the config for a commit is compiled and validated
*before* that commit's build runs. If the config is broken or unfetchable, the build is
**held off** for that cycle and the live pipeline is left **unchanged**. Consequence: a bad
config never ships on stale code — you can't half-apply a broken change. Fix the config, push
again, and the next cycle proceeds.

## Git-only: there are no config-mutation endpoints

Your repo is the **only** writer of config. There is **no way to mutate shape** out of band —
no way to create, delete, reorder, or reconfigure steps, and no way to create or delete triggers.
Pipeline shape comes solely from the file via reconcile, so the live pipeline can never drift
from your repo. (`pl` has no shape-mutation commands by design; earlier API versions exposed such
endpoints and rejected them once a pipeline was bound — now that every pipeline is bound at
creation, they don't exist.)

**Operational commands work on a bound pipeline** (they're not config):

- Manual production trigger — `pl production trigger <pipelineId>`
- Job cancel — `pl job cancel <jobId>`
- Setting secret **values** — `pl secret set <pipelineId> <name>`
- The [deploy-trigger mode](#deploy-trigger-mode) — `pl binding set-trigger <pipelineId> <mode>` (how it deploys, not what it deploys)
- The [promotion gate](promotion-gate.md) — `pl processing block` / `unblock` / `re-promote` on a processing step

The dividing line: **shape** is git-owned; **operations** are `pl`-driven.

## Reading the status

```sh
pl binding status <pipelineId>
```

```text
Pipeline:  <id>
Repo:      you/my-app@main (.pipelines)
Reconcile: Success (last sync 2026-06-16 12:00:00Z)
Deployed:  abc123…
Synced:    abc123…
Secrets:
  NAME          SET  DESCRIPTION
  GITHUB_TOKEN  set  Read token to fetch the repo tarball during build.
```

Add `--json` for the machine-readable form (same fields: `result`, `lastSyncTime`, `problems`,
`lastSyncedSha`, `lastDeployedSha`, and `secrets[]`).

### Reconcile result

| Value | Meaning |
|---|---|
| `NeverRun` | Freshly bound; no reconcile has happened yet. |
| `Success` | The last reconcile applied the repo config cleanly. |
| `Error` | The last reconcile failed (fetch / compile / apply / drain). **Live state is unchanged.** Read the listed problems. |

### Secret state

Computed **live** at read time (not stored), so a just-set secret reflects immediately:

- `set` — the secret is set in the pipeline's k8s secret.
- `unset` — declared but not set. Set it: `pl secret set <pipelineId> <name>`.
- `unknown` — k8s could **not be read** (unconfigured/unreachable). This is *unknown*, **not**
  "missing" — don't treat it as unset.

## See also

- [`config.yaml` Reference](config-reference.md) — what gets reconciled
- [Troubleshooting](troubleshooting.md) — what `result: Error` problems mean
