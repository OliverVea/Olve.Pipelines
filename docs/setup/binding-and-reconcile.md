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
| `head` | the newest commit on the branch (`sha`, `message`, `committedAt`) as the last push check read it; null for a pipeline without an `onPush` trigger |
| `status` | last reconcile `result`, `lastSyncTime`, `problems`, `warnings`, `sha`, `inProgress`, `versionNumber` (the version it switched to) |

| `pl` command | Purpose |
|---|---|
| `pl binding create <repo> [--branch <b>] [--path <dir>] [--credentials-secret <key>]` | Create a pipeline bound to a repo. Rolls the pipeline back if the bind fails. |
| `pl binding get <pipeline>` | Show the binding |
| `pl binding status <pipeline>` | Reconcile result, commit, in-progress flag, resulting and active version, problems and warnings, the branch's newest commit |
| `pl binding set-credentials <pipeline> [key]` | Set (or, with no key, clear) the token key |
| `pl binding set-branch <pipeline> <branch>` | Move the binding to another branch of the same repo |
| `pl binding set-polling <pipeline> on\|off` | Stop (or resume) polling GitHub for the binding |
| `pl binding reconcile <pipeline> [--rebuild]` | Reconcile now, off the poll schedule (also retries a commit whose reconcile failed); `--rebuild` reconciles the applied commit again, for a server change in how manifests are read |

The pipeline's name and description come from `pipeline({ name, description })` at reconcile; the
bind seeds a provisional name from the repo.

### How changes are noticed

The service polls every binding's branch every 15 minutes (an unchanged branch costs a free
conditional request). When the instance has `Webhooks:PublicBaseUrl` set and the binding has a
`credentialsSecret` token with `admin:repo_hook`, it also registers a GitHub hook for `push` and
`pull_request` events, so changes are picked up within seconds; deliveries are authenticated by an
HMAC secret the binding generates.
A binding with polling off (`pl binding set-polling <pipeline> off`) never polls: it syncs only on
`pl binding reconcile` (`POST …/binding/reconcile`) or its webhook, and its push trigger fires only
from the webhook. Use it for a pipeline something else drives, such as a test fixture another
pipeline reconciles and triggers. When `Webhooks:PublicBaseUrl` changes, the registered hooks
are pointed at the new URL (a hook deleted on GitHub is registered again); clearing it deletes them.

The binding also keeps track of its repository's pull requests into its branch by the authors in
`Ci:Authors` (not those from forks),
whether or not the pipeline declares CI: `GET /api/pipelines/{id}/ci/pulls` lists them, open or
closed. The `pull_request` deliveries keep them current, and the service reads the open ones from
GitHub at startup, when a binding is created or its branch changes, and when its hook is registered,
so a delivery it missed is caught up. Reading them needs no more access than the binding already has.

When the pipeline declares CI (`export const ci = checks(…)` next to the pipeline), an open pull
request gets a CI run once its head has been still for 60 seconds: a burst of pushes makes one run,
for the last of them. The run tests GitHub's merge commit of the pull request onto the branch, with
the active version's CI definition and binary, and a new push supersedes the running run. A pull
request that GitHub can't merge gets no run and shows as a conflict. A pull request that changes the
pipeline's definition (the binding path or its local dependencies) runs with its own: its tested
commit is built and described on its own (a `olve-ci-build-…` Job), never becoming a version, and
the run's steps start once it is. A pull request that adds CI is checked this way before it merges;
one whose definition declares no CI gets no run; a build or describe that fails, or a definition that
doesn't validate, fails the run with the problems. Closing or merging a pull request cancels its
running run and the build it waits for. CI runs are numbered per
pipeline on their own, and `GET /api/pipelines/{id}/ci/runs/{number}` shows one, its steps and their
logs, files and outputs; `POST …/ci/pulls/{pull}/runs` runs a pull request again.

When `main` moves, open pull requests keep their result, marked stale. When the active version checks
pull requests differently (its `ci` section or its code hash changed), each open, ready pull request
whose latest run used an older version's checks (not its own build) runs again by itself. These runs
go after pull requests' own runs and before drafts; drafts run when they are marked ready.

A step that already succeeded in a CI run of the same tree (the tested commit's tree, whatever the
commit) with the same code (the definition's `codeHash`) isn't run again: the new run records it as
succeeded, with `reusedFrom` naming the attempt that ran, whose output, files and log it shows. No Job
runs for it. Failed, skipped and cancelled steps always run again, so running a pull request again
after a failure runs only what didn't succeed. This relies on a rule for CI steps: no output may depend
on which commit with that tree it ran on. `gitHubSource` reports the commit it fetched, so a reused
source step names the earlier commit, and a step that puts `ctx.run.version` into its output keeps the
earlier run's.

CI reports the commit status `olve/ci` on each pull request's head, linking the run's page:

| When | Status |
|---|---|
| A push, while the run waits to start | `pending` "Waiting to start" |
| The run is going | `pending` "3 of 8 steps done" |
| It succeeded | `success` "Succeeded in 6m12s" |
| It failed | `failure` "Failed: test" (the failed steps, or the problem with the definition) |
| It was cancelled, and the pull request is still at that head | `error` "Cancelled": no result; running it again replaces it |
| A waiting head doesn't merge onto the branch | `error` "Doesn't merge onto 'main'" |
| A waiting head turns out to have no CI | `success` "No CI to run" |

A push that changes the pipeline's definition gets nothing until its own build shows whether its
definition keeps CI, so a pull request whose definition removes CI gets no status. A superseded run
reports nothing more (the run replacing it reports), and neither does a run cancelled because its pull
request closed or merged. Statuses go out in the background; a newer state
replaces one not yet sent, and a GitHub server error is tried again. This needs the binding's token to
have **Commit statuses: write** (a classic token: `repo`). Without it, `pl binding status` shows a
warning saying so until a status gets through.

### The merge queue

A pull request enters the pipeline's merge queue with `POST /api/pipelines/{id}/ci/queue` (`{pull}`)
or when GitHub auto-merge is turned on for it; `GET …/ci/queue` lists it, and `DELETE …/ci/queue/{pull}`
takes one out. A draft, a pull request GitHub can't merge, one from a fork, by an author not in `Ci:Authors` or into another branch, and
any pull request of a pipeline that declares no CI are refused.

The queue tests the pull requests at its head together, up to 5: it creates a branch
`olve-queue/<run>` at the binding branch's head, merges each one's head into it in queue order, and
runs CI on the result as a queue run (it goes before pull requests' runs for Job slots, and stops at
its first failed step). When it passes, each pull request gets the status `olve/merge-queue` success
and is merged by rebase at the head that was tested, one after another, and only while the branch is
at the tree the batch had before it; after each merge the branch must be at the tree the batch tested
with it. When either doesn't hold (the branch moved outside the queue), landing stops and the rest is
tested again on the new head; the branch moving while a batch is tested supersedes its run the same
way. When a batch fails, a single pull request leaves the queue with `olve/merge-queue` failure
naming the failed step ("Removed: test failed"); a larger batch is split in halves, until the one that
breaks it is found. A push to a queued pull request, closing it, retargeting it, turning it back into a
draft or turning off the auto-merge that queued it takes it out too, and cancels a run testing it.
While auto-merge is on, a new head (a fix pushed after a failure, or the push that took it out),
leaving draft or reopening puts the pull request back in; one removed at the same head stays out.

So that only the queue merges into the branch, require the status check **`olve/merge-queue`** (not
`olve/ci`) in the branch's protection. With auto-merge turned on, GitHub then waits for the queue,
which sets that status on the head it tested right before it merges. The queue needs the binding's
token to have **Contents: write** (the batch branch and the merges; a classic token: `repo`), and
**Workflows: write** too when pull requests change `.github/workflows/`. Without it, `pl binding status`
shows a warning saying so.

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

A value can carry its expiry: `pl secret set <pipeline> NPM_TOKEN --expires 90d` (or a date,
`--expires 2027-01-25`). From 14 days before, the pipeline's overview and the pipeline list say which
secret to replace, and `pl secret list` shows the date; nothing is deleted when it passes. Setting the
value again without `--expires` clears the date, deleting the secret removes it, and a copy carries it.
The date is kept by the service beside the value, so editing the Kubernetes Secret directly doesn't
change it.

## See also

- [Pipeline Reference](pipeline-reference.md) — what gets reconciled
- [Runs & Operations](runs-and-operations.md) — what happens after a trigger
- [Troubleshooting](troubleshooting.md) — reconcile errors
