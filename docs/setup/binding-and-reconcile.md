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
| `lastReconciledSha` / `lastPushSha` | the config commit last reconciled / the branch head last turned into a push trigger |
| `status` | last reconcile `result`, `lastSyncTime`, `problems`, `warnings`, `sha`, `inProgress`, `versionNumber` (the version it switched to) |

| `pl` command | Purpose |
|---|---|
| `pl binding create <repo> [--branch <b>] [--path <dir>] [--credentials-secret <key>]` | Create a pipeline bound to a repo. Rolls the pipeline back if the bind fails. |
| `pl binding get <pipeline>` | Show the binding |
| `pl binding status <pipeline>` | Reconcile result, commit, in-progress flag, resulting and active version, problems and warnings |
| `pl binding set-credentials <pipeline> [key]` | Set (or, with no key, clear) the token key |
| `pl binding reconcile <pipeline>` | Reconcile now, off the poll schedule (also retries a commit whose reconcile failed) |

The pipeline's name and description come from `pipeline({ name, description })` at reconcile; the
bind seeds a provisional name from the repo.

### How changes are noticed

The service polls every binding's branch every 15 minutes (an unchanged branch costs a free
conditional request). When the instance has `Webhooks:PublicBaseUrl` set and the binding has a
`credentialsSecret` token with `admin:repo_hook`, it also registers a GitHub `push` hook, so changes
are picked up within seconds; deliveries are authenticated by an HMAC secret the binding generates.
There is nothing to choose per binding. When `Webhooks:PublicBaseUrl` changes, the registered hooks
are pointed at the new URL (a hook deleted on GitHub is registered again); clearing it deletes them.

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
      `bun install --frozen-lockfile` and `olve-build`, uploads the compiled binary (a zstd-compressed tar,
      ~42 MB) to storage and prints the manifest.
   2. **Validate** the manifest with every [reconcile check](pipeline-reference.md#checks-that-reject-a-pipeline).
      Errors fail the reconcile; warnings are reported.
   3. If the manifest matches the active (or latest) version, reuse it; otherwise add a **new
      version** (with step and model entity versions). Then **switch** to it, if the pipeline's
      config document and the secrets it references satisfy the version; otherwise the version is
      **pending** ([below](#pending-versions)).
2. **Runs.** If the branch head moved past `lastPushSha`, record it and fire the `onPush`
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

## Pending versions

A version whose config isn't satisfied is stored as **pending**: a required config entry is missing
or invalid, or a secret the config references (`secretRef`) isn't set. The active version stays
active. Saving config (`pl config set`) or setting the secret re-checks, and the version activates
on its own once satisfied; the push that produced it then starts its run. While a version is
pending, automatic triggers wait (config-before-run); manual triggers start on the active version.
Details: [Pipeline Config](configuration.md#versions-pending-until-satisfied).

## Switch and rollback

One version is **active**: new runs start on it. Switching is immediate:

```
Active(7)  ──switch 8──>  Active(8)
```

```sh
pl pipeline switch my-app 7                    # rollback = switch to an older version
pl pipeline switch my-app 7 --cancel-running   # also cancel the runs still on other versions
```

- Reconcile switches automatically. A manual `switch` (e.g. a rollback) holds until the next config
  commit is reconciled — that switches to the commit's version again, even if it is unchanged.
- A switch is refused, with the reasons, when the pipeline's config doesn't satisfy the target
  version. A successful switch drops any pending version.
- Runs already going carry on with the version they pinned; nothing is cancelled. Runs never
  overtake each other: a run doesn't start a step a newer run already started, it is superseded
  instead, so an old-version run can't deploy after a newer one.
- Continue and step rerun need the run's version to be active; on an older version, trigger a new run.
- `--cancel-running` cancels the runs still on other versions and their in-flight attempts; requests
  that arrive in the moment it takes are queued and start on the new version.

## Git-only

There are no API endpoints or `pl` commands that change pipeline shape. Steps, stages, triggers and
secrets declarations come only from your committed pipeline. Operations are allowed on a bound
pipeline: `pl trigger`, `pl run cancel|rerun|continue`, `pl step rerun`, `pl step block|unblock`,
`pl pipeline switch`, `pl secret set`, `pl binding set-credentials|reconcile`.

## Reading the status

```sh
pl binding status <pipelineId>
```

```text
Pipeline:    <id>
Repo:        OliverVea/Olve.Pipelines@main (.pipelines)
Reconcile:   Success (last sync 2026-10-22 08:10:00Z)
  commit:    <sha> (in progress)
  version:   v14
Active:      active v14
Pending:     pending v15: config config.target.host: is required
Last push:   <sha>
Reconciled:  <sha>
```

`commit` is the config commit of the last attempt, `version` the version it produced (switched to,
or pending). `Pending` shows only while a version waits for its config or secrets. Which
declared secrets are set: `pl secret list <pipeline>`.

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
