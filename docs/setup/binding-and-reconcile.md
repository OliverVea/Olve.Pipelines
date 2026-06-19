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

### Binding endpoints

`with-repo` is the **only** way to create a pipeline — there is no unbound/draft create. Every
pipeline is bound to a repo from birth, so its shape always comes from git.

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/pipelines/with-repo` | Create a pipeline already bound to a repo (rolls back the pipeline if the bind fails). Body: `{ name, repo, branch?, path?, credentialsSecret?, deployTrigger? }` |
| GET | `/api/pipelines/{id}/binding` | Get the binding |
| GET | `/api/pipelines/{id}/binding/status` | Reconcile result/problems + live secret set/unset |
| PATCH | `/api/pipelines/{id}/binding/deploy-trigger` | Change the [deploy-trigger mode](#deploy-trigger-mode). Body: `{ deployTrigger }` |

`branch` defaults to `main`, `path` to `.pipelines`. `deployTrigger` defaults to `webhook`.

### Deploy-trigger mode

How a bound pipeline learns it should deploy. Mutable at any time via the PATCH endpoint above.
The enum serializes as an integer: `0` Webhook, `1` WebhookOnly, `2` Poll.

| Mode | Value | Behavior |
|---|---|---|
| Webhook | `0` (default) | Auto-registers a GitHub `push` hook that runs reconcile+deploy on each push; the poll still runs as a **15-min safety net** (catches a missed/failed delivery). |
| WebhookOnly | `1` | Webhook only — polling is suppressed once the hook is confirmed live. The explicit "opt out of polling" mode. |
| Poll | `2` | No webhook; the 15-min poll is the sole trigger. |

Webhook modes need **`Webhooks:PublicBaseUrl`** configured server-side (so GitHub can reach the
receiver) and the binding's **`credentialsSecret`** token to carry **`admin:repo_hook`** (it both
fetches the repo and manages the hook). The inbound delivery is authenticated by an HMAC secret the
binding generates — there's no inbound secret to manage. If a hook can't be registered (no public
URL, no credentials, registration failure), **polling continues regardless of mode** so deploys
never silently stop. The webhook path runs the same config-before-build cycle as the poll.

> Pre-existing bindings (created before deploy-trigger modes) load as **Poll** — they keep their
> current behavior and don't silently adopt webhooks. Switch them with the PATCH endpoint.

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

Your repo is the **only** writer of config. The API has **no endpoints that mutate shape** —
there is no way to create, delete, reorder, or reconfigure steps, and no way to create or delete
triggers over HTTP. Pipeline shape comes solely from the file via reconcile, so the live pipeline
can never drift from your repo. (Earlier versions exposed these endpoints and rejected them once a
pipeline was bound; now that every pipeline is bound at creation, the endpoints don't exist.)

**Operational endpoints stay open** (they're not config):

- Manual production trigger — `POST /api/pipelines/{id}/trigger/production`
- Job cancel — `POST /api/jobs/{id}/cancel`
- Setting secret **values** — `PUT /api/pipelines/{id}/secrets/{name}`
- The [deploy-trigger mode](#deploy-trigger-mode) — `PATCH /api/pipelines/{id}/binding/deploy-trigger` (how it deploys, not what it deploys)
- The [promotion gate](promotion-gate.md) — brake / unblock / re-promote on a processing step

The dividing line: **shape** is git-owned; **operations** are API-allowed.

## Reading the status

```http
GET /api/pipelines/{id}/binding/status
```

```jsonc
{
  "pipelineId": "…",
  "repo": "you/my-app",
  "branch": "main",
  "path": ".pipelines",
  "lastDeployedSha": "abc123…",
  "lastSyncedSha": "abc123…",
  "result": "Success",
  "lastSyncTime": "2026-06-16T12:00:00Z",
  "problems": [],
  "secrets": [
    { "name": "GITHUB_TOKEN", "description": "…", "isSet": true }
  ]
}
```

### `result` values

| Value | Meaning |
|---|---|
| `NeverRun` | Freshly bound; no reconcile has happened yet. |
| `Success` | The last reconcile applied the repo config cleanly. |
| `Error` | The last reconcile failed (fetch / compile / apply / drain). **Live state is unchanged.** Read `problems`. |

### `secrets[].isSet`

Computed **live** at read time (not stored), so a just-set secret reflects immediately:

- `true` — the secret is set in the pipeline's k8s secret.
- `false` — declared but not set. Set it: `PUT /api/pipelines/{id}/secrets/{name}`.
- `null` — k8s could **not be read** (unconfigured/unreachable). This is *unknown*, **not**
  "missing" — don't treat it as unset.

## See also

- [`config.yaml` Reference](config-reference.md) — what gets reconciled
- [Troubleshooting](troubleshooting.md) — what `result: Error` problems mean
