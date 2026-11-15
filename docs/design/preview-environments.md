# Preview environments

Status: partly built. The server and the SDK (2.0.0-beta.40) have it: `kind`/`pull` in the CI input,
`reuse: false`, `onClose` close runs, Preview records and `olve/preview`; `Ci:Authors` and no
`clusterAccess` in CI are live (see [CI](ci.md)). The UI's preview cell, the homelab pieces and
Trains are not built.

A preview environment is a live instance of an app for one open pull request, deployed by the PR's
CI runs and torn down when the PR closes or merges. Previews are reachable over the tailnet only.
They build on [CI](ci.md): a preview is one or more steps of the pipeline's `checks()` and the
`onClose` stage that removes them.

## Example

```ts
export const ci = checks({ secrets: [PREVIEW_KUBECONFIG] })
  .stage([source, (from) => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(imageBuild, test)
  .when({ kind: 'pull' }, (c) => c.stage(deployPreview).stage(smokePreview))
  .onClose(removePreview);
```

`deployPreview`, `smokePreview` and `removePreview` are steps of the pipeline (custom until several
pipelines use them). They run helm with `PREVIEW_KUBECONFIG`. `deployPreview` installs the
release `<app>-preview-<pull>` into the preview namespace and links its URL; `removePreview`
uninstalls it.

## What CI gains

### The run kind and the PR number in the input

`CiInput` grows to `{ repo, sha, kind, pull }`: `kind` is `pull`, `queue` or `close`, `pull` is the
PR number (0 for a queue run). `when({ kind: 'pull' })` keeps previews out of queue runs with the
`when` CI already has.

### Steps that are never reused

`step({ …, reuse: false })`. CI always runs such a step, even on a tree it already passed. CD
ignores the flag.

Reuse ignores a step's input, so a step that reads the PR, or reads what a deploy produced, would be
reused from another PR with the same tree and test nothing. The validator refuses, from the tagged
mappings, a CI step that is reused and whose input reads `input.pull` or the output of a
`reuse: false` step. `deployPreview` and `smokePreview` above are both `reuse: false`. Because the PR
number is input and not a `ctx.ci.pull` read at run time, this is a static check.

A failed preview step fails the run like any step, so `olve/ci` goes red.

### No cluster access in CI

A CI step never gets `clusterAccess` (see [CI](ci.md)); previews deploy with a pipeline secret
(below).

### Teardown: `onClose`

`checks().onClose(...)` declares one stage that removes a preview. It runs as a CI run of its own,
kind `close`, when the PR closes or merges. It uses the
definition and binary of the run that deployed the preview, so it removes what was deployed, even
when later runs changed the definition or dropped `onClose`. Its input is `CiInput` with
`kind: 'close'`. `onClose` steps are `reuse: false` implicitly.

A close run is tracked like any CI run: attempts, logs, a restart resumes it. It is not part of the
PR's CI:

- It never reports `olve/ci` and is left out of every "latest run of the PR" rule in [CI](ci.md):
  the status, the re-report at startup, `stale`, the PR list and the failing count.
- A push to the PR doesn't supersede it. A PR run's preview step waits for the PR's running close
  run to end, so a teardown never removes a preview that was just deployed again.
- A failed close run is tried again with backoff (1, 5, 15, then every 60 minutes) while the PR
  stays closed, and shows as a warning on the binding until it succeeds.
- A close run's steps start only once no `reuse: false` attempt of the PR still has a Job: a
  cancelled deploy's helm may run on for a moment, and a teardown before it ends would leak the
  preview.

A PR reopened at the head it closed at keeps that head's run, so its preview comes back with the next
push or `pl ci rerun`.

The steps in `onClose` read only the input: `removePreview` needs the PR number, not what the deploy
produced. The deploying run's binary is used so the teardown is the code that matches the deploy.

Retention keeps the binary of each preview's deploying run, CI builds included, so teardown can
always run.

### Previews

A **Preview** (pipeline, PR) is the server's record of a preview. Only a pipeline whose `checks()`
declares `onClose` has previews. A preview is created when a `reuse: false` attempt of a PR run
starts, before anything is deployed, so a deploy that fails or is cancelled halfway is still torn
down. It holds the run that last deployed it, the last deploy time, the URL (the step's link) and its
state: `Deploying`, `Live`, `Removing`, `Removed`, `Failing` (teardown failing, retried).

A preview lives until its PR closes or merges; there is no cap. RAM is the node's limit, and a Trains
preview requests 32 Mi and is limited to 128 Mi.

### Later: cap and retirement

Built when previews' RAM becomes a problem, not before.

- **Cap:** `checks({ previews: { max } })`. When a PR run would deploy a preview beyond the cap, the
  preview of the least recently pushed PR is retired first, drafts before ready PRs, and the deploy
  waits for it.
- **Idle retirement:** a preview whose PR hasn't been pushed for `idleDays` is retired. The next
  push deploys it again.
- **Draft:** converting a PR to draft retires its preview.
- Retiring is a close run, without closing the PR; `POST pulls/{number}/preview/remove` retires one
  by hand.

At the cap, the waiting attempt has no Job yet, so its `timeoutSeconds` hasn't started; the run view
says what it waits for. Only a failing teardown fails a deploy; anything else waits, and every wait
ends on its own:

| Case | Rule |
|---|---|
| What counts | Every preview not `Removed`: `Deploying`, `Live`, `Removing`, `Failing` all hold RAM. A PR redeploying its own preview is not a new one. |
| Which is retired | Only a `Live` preview; one `Deploying` belongs to a run in progress, one `Removing` is already going. |
| None is `Live` | The deploy waits; each preview that finishes deploying or removing makes the choice again. |
| The retired preview's close run fails | The deploy fails: "preview cap 5 reached; the preview of #2 is failing to be removed". No other preview is retired: the failing one still counts. |
| Two PRs over the cap at once | Retirements are chosen under the run scheduling lock: each new preview retires a different one. |
| The waiting PR is pushed again | Its run is superseded as usual; the retirement goes on, and the freed slot goes to whichever deploy comes first. |

The cap is per pipeline, set to what the app costs. No RAM-aware scheduling.

## Reaching a preview

- **Over the tailnet only.** A preview's DNS record is public but unproxied and points at the node's
  Tailscale IP, like the homelab's other tailnet hosts; nothing goes through Cloudflare's tunnel.
- **Hostname:** `pr-<n>.<app>-preview.ovea.pro` (e.g. `pr-58.trains-preview.ovea.pro`). The homelab
  chart holds, per app, one wildcard DNS record pointing at the Tailscale IP and one wildcard
  Certificate (Let's Encrypt through DNS-01) issued in the preview namespace, since an Ingress reads
  its TLS Secret from its own namespace. A preview is reachable as soon as it is routed, and creating
  one issues no certificate.
- **Routing:** the preview release renders its own Ingress for its host, with the wildcard Secret
  (`<app>-preview-wildcard`, named by the homelab chart; the app's pipeline passes the name). This
  breaks the convention that only the homelab chart owns Ingresses, on purpose: previews come and go
  with PRs. App charts get an optional `ingress` value, off by default. external-dns ignores the
  `previews` namespace: the wildcard record already routes every preview host.
- **Namespace:** previews live in a namespace of their own (`previews`), so the preview credential
  can stop there. The app chart labels every resource of a preview release `olve-preview=<app>`, so
  previews can be listed and swept by label.
- **Finding it:** an `olve/preview` commit status on the PR's head links the preview: pending while
  deploying, success with the URL when live, error ("Deploy failed, see olve/ci") when the deploy
  fails, and after teardown success with the description "Preview removed" and no link. It is never
  `failure`: the deploy's failure is `olve/ci`'s to report. The CI tab shows each PR's preview, its
  state and link.
- **Advisory, not a gate.** Branch protection requires only `olve/merge-queue`, and queue runs never
  deploy a preview. A failing preview turns `olve/ci` red but doesn't stop a merge: the preview is for
  people to look at, and tests that gate a merge run in the queue run.

## Who gets CI

CI runs a pull request's own code, previews included, so only trusted authors get it. Many of the
repos are open source.

- `Ci:Authors`: the GitHub logins whose pull requests get CI, a server setting, one list for every
  binding on the instance. The deployer's pipeline config holds it per instance (`ci.authors`, next
  to `auth.allow.users`, set with `pl config`), and its deploy passes it to the server. A pull
  request by anyone else is treated like one from a fork: no runs, no statuses, no preview, and
  enqueueing is refused. Unset, no pull request gets CI.
- It is server configuration, not part of `checks()`: a pull request can change its own definition,
  so a list in the repo would be edited by the very PRs it keeps out.
- Bots are authors like anyone else; Renovate's PRs get CI only if its login is listed.

## Secrets

The preview steps deploy with a pipeline secret, `PREVIEW_KUBECONFIG`: a kubeconfig for a
ServiceAccount whose Role covers the `previews` namespace and nothing else. It is on CI's
`checks({ secrets })` allow-list, so a PR's code can read it and can do anything in `previews`.
That adds little: a preview runs the PR's code anyway. It holds because only listed authors reach
CI, and because `previews` holds nothing of value: a NetworkPolicy per release, no Secrets but the
wildcard certificates of preview hosts, whose keys let someone impersonate
`*.<app>-preview.ovea.pro` on the tailnet and nothing more. No CD credential belongs on the allow-list.

## Pieces and order

Server first, then the SDK, then a pipeline.

1. Server and manifest: `reuse: false` and the reuse rule, `CiInput.kind`/`pull`, `onClose` and close
   runs, the Preview entity, `olve/preview`.
2. API: previews on the PR views (`GET pulls`, `GET pulls/{number}`). Spec diff for approval first.
3. SDK: `reuse`, `onClose`, the wider `CiInput`. Version bump. The `ovea-olve-pipelines`
   skill learns previews; the `ovea-workflow` plugin version is bumped.
4. UI: the preview cell in the CI tab, mock first.
5. Homelab: the `previews` namespace (left out by external-dns), the preview ServiceAccount, its Role
   and a kubeconfig for it (set with `pl secret set`), a wildcard Certificate
   (`<app>-preview-wildcard`) and DNS record per app.
6. Trains: after its PR checks move from GitHub Actions to `checks()`. Its chart gains the optional
   Ingress and the `olve-preview` label; its pipeline gains `deployPreview`, `smokePreview` and `removePreview`.
7. Later: the cap and retirement, when previews' RAM calls for them.
