# Binding, Reconcile & Versions

[← Index](index.md) · [Subject Index](subjects.md)

How a pipeline is tied to your repo, how a commit becomes a pipeline version, how the service
switches between versions, and how to read the status.

## The binding

A **binding** ties one pipeline to one repo + branch + path. It is the only way to create a
pipeline; every pipeline has exactly one.

| Field | Meaning |
|---|---|
| `repo` | `owner/name` on GitHub |
| `branch` | branch to watch (default `main`) |
| `path` | pipeline directory in the repo (default `.pipelines`) |
| `credentialsSecret` | the **key name** in the pipeline's Secret holding a GitHub token (omit for a public repo) — a reference, never the token |
| `deployTrigger` | `webhook`, `webhook-only` or `poll` (see below) |
| `lastSyncedSha` / `lastDeployedSha` | the config commit last reconciled / the branch head last turned into a push trigger |
| `status` | last reconcile `result`, `lastSyncTime`, `problems`, `warnings` |

| `pl` command | Purpose |
|---|---|
| `pl binding create <repo> [--branch <b>] [--path <dir>] [--credentials-secret <key>] [--trigger <mode>]` | Create a pipeline bound to a repo. Rolls the pipeline back if the bind fails. |
| `pl binding get <pipelineId>` | Show the binding |
| `pl binding status <pipelineId>` | Reconcile result and problems, plus which declared secrets are set |
| `pl binding set-credentials <pipelineId> [key]` | Set (or, with no key, clear) the token key |
| `pl binding set-trigger <pipelineId> <mode>` | Change the deploy-trigger mode |
| `pl binding reconcile <pipelineId>` | Reconcile now, off the poll schedule (also retries a commit whose reconcile failed) |

The pipeline's name and description come from `pipeline({ name, description })` at reconcile; the
bind seeds a provisional name from the repo.

### Deploy-trigger mode

How the service learns the branch moved:

| Mode | Behaviour |
|---|---|
| `webhook` (default) | Registers a GitHub `push` hook; the 15-minute poll still runs as a safety net. |
| `webhook-only` | Webhook only; polling stops once the hook is confirmed live. |
| `poll` | No webhook; the 15-minute poll is the only trigger. |

Webhook modes need `Webhooks:PublicBaseUrl` set on the instance and a `credentialsSecret` token
with `admin:repo_hook`. Deliveries are authenticated by an HMAC secret the binding generates. If a
hook can't be registered, polling continues regardless of mode. **v2 beta has no public webhook
route, so bind with `--trigger poll` there.**

Either way, a branch-head change fires the pipeline's **`onPush`** trigger. `onPoll` triggers are
separate: they are declared in `pipeline.ts` and poll their own URL.

## The reconcile cycle

Per binding, the deploy poll (or a webhook, or `pl binding reconcile`) does, in this order:

1. **Config.** Ask GitHub for the newest commit touching the binding path or one of its local
   package dependencies (`file:` / `link:` entries in its `package.json`, e.g. `file:../sdk`) — one
   conditional request per path, free when unchanged. If that commit hasn't been applied, **reconcile** it:
   1. Start a **build Job** `olve-build-<pipeline12>-<sha12>` on `oven/bun:<pinned>-slim` (under the
      runner hardening: gVisor, PSS baseline, NetworkPolicy). It fetches the repo tarball at the
      commit (anonymously, or with the binding's token key, default `GITHUB_TOKEN`), runs
      `bun install --frozen-lockfile` and `olve-build`, uploads the compiled binary (`step.tar.gz`,
      ~44 MB) to storage and prints the manifest.
   2. **Validate** the manifest with every [reconcile check](pipeline-reference.md#checks-that-reject-a-pipeline).
      Errors fail the reconcile; warnings are reported.
   3. If the manifest matches the active (or latest) version, reuse it; otherwise add a **new
      version** (with step and model entity versions). Then **switch** to it.
2. **Runs.** If the branch head moved past `lastDeployedSha`, record it and fire the `onPush`
   trigger. The first observation only records the head.

Both steps run under one per-binding lock, so a run for a commit always starts after that commit's
reconcile and switch (**config-before-run**). While the newest config commit is unapplied, runs are
held.

- A commit that touches neither the binding path nor its local dependencies makes no version — the
  run starts on the active one.
- A reconcile that fails is **not retried** by the background poll. Push a fix, or
  `pl binding reconcile <pipelineId>` to retry.
- A failed reconcile changes nothing: no version, no switch; the active version keeps running.
- The service never executes your TypeScript itself — describe, handlers and trigger mappers run
  only in sandboxed Jobs.

## Versions

```sh
pl pipeline versions my-app                 # versions, commit, step/model versions; > marks the one runs start on
pl pipeline manifest my-app [--version 3]   # a version's manifest (--json: the canonical JSON)
```

- A **pipeline version** is a monotonic integer per pipeline, linked to the commit that produced it.
  Versions are immutable and kept while any retained run references them.
- **Steps and models** have their own versions (`deploy-beta.3`), bumped only when that entity
  changes — see [what makes a new version](pipeline-reference.md#what-makes-a-new-version).
- A run **pins** one version: it always runs, renders and validates against that version, even after
  newer ones exist.

The UI's pipeline view shows the versions, a manifest diff between any two, and a switch button.

## Switch and rollback

Only one version runs at a time. Changing it is one operation:

```
Active(7)  ──switch 8──>  Draining(7 → 8)  ──no Scheduled/InProgress attempts on 7──>  Active(8)
```

```sh
pl pipeline switch my-app 7                    # rollback = switch to an older version
pl pipeline switch my-app 7 --cancel-running   # cancel the old version's in-flight work instead of waiting
```

- Reconcile switches automatically. A manual `switch` (e.g. a rollback) holds until the next config
  commit is reconciled — that switches to the commit's version again, even if it is unchanged.
- While draining, the old version starts no new attempts and in-flight attempts finish. New triggers
  are **queued** (latest-wins collapses them) and start on the new version, mapped and validated
  against *its* triggers and input model.
- Runs paused on the old version (held at a gate, waiting to be continued) don't block the drain and
  can't be continued afterwards — re-trigger them on the new version.
- There is no side-by-side canary of two definitions.

## Git-only

There are no API endpoints or `pl` commands that change pipeline shape. Steps, stages, triggers and
secrets declarations come only from your committed pipeline. Operations are allowed on a bound
pipeline: `pl trigger`, `pl run cancel|rerun|continue`, `pl step rerun`, `pl gate block|unblock`,
`pl pipeline switch`, `pl secret set`, `pl binding set-trigger|set-credentials|reconcile`.

## Reading the status

```sh
pl binding status <pipelineId>
```

```text
Pipeline:  <id>
Repo:      OliverVea/Olve.Pipelines@v2 (.pipelines)
Reconcile: Success (last sync 2026-10-22 08:10:00Z)
Deployed:  <sha>
Synced:    <sha>
Secrets:
  NAME             SET  DESCRIPTION
  GITHUB_TOKEN     set  GitHub read token used to fetch the repo tarball.
  SSH_PRIVATE_KEY  set  SSH key for importing the image and running helm on the homelab host.
```

`--json` adds `warnings`, `reconcileSha` (the commit of the last attempt) and `reconcileInProgress`.

| Result | Meaning |
|---|---|
| `NeverRun` | Freshly bound; no reconcile yet. |
| `Success` | The last reconcile built, validated and (if the manifest changed) switched. |
| `Error` | The last reconcile failed (fetch, build, describe or validation). Nothing changed. Read the problems. |

Secret state is computed live: `set`, `unset` (declared but not set — `pl secret set`), or `unknown`
(Kubernetes couldn't be read; not the same as unset).

## See also

- [Pipeline Reference](pipeline-reference.md) — what gets reconciled
- [Runs & Operations](runs-and-operations.md) — what happens after a trigger
- [Troubleshooting](troubleshooting.md) — reconcile errors
