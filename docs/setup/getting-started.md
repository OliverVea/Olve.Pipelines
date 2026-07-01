# Getting Started — put a repo under CD

[← Index](index.md) · [Subject Index](subjects.md)

This is the end-to-end path: from a repo with no CD to a pipeline that builds and deploys on
every push. It is entirely **GitOps** — you author one file in your repo and run one `pl binding
create`; you never author steps or triggers by hand.

Everything here is driven by the **`pl` CLI** — the Olve.Pipelines operator tool. Use it for all
create/bind, secret, status, and trigger operations; you never call the HTTP API directly.

## Prerequisites

- A Git repository on GitHub the server can read (public, or private with a read token).
- The container images your steps need (e.g. a Kaniko build image, an `alpine` deploy image).
- The **`pl` CLI**, logged in. Download it from the instance (`GET /download/{asset}`) and
  authenticate once:
  ```sh
  pl login                                           # prod (pipelines-private.ovea.pro)
  pl login --api-url https://pipelines-beta.ovea.pro # a specific environment
  ```
  `pl login` runs a browser OIDC flow (auth-code + PKCE) and caches the token in `~/.pl`. Add
  `--device` to log in by QR code without a local browser.

## Step 1 — Add `.pipelines/config.yaml` to your repo

Create `.pipelines/config.yaml` at the root of your repository. This file is the **single
source of truth** for the pipeline's shape.

```yaml
apiVersion: "0.0"            # major must match the server (currently 0.x)
name: my-app

secrets:                     # declared by NAME ONLY — values never live in the repo
  - name: GITHUB_TOKEN
    description: Read token to fetch the repo tarball during build.

productionSteps:             # run in parallel → bundle/<step-name>/
  - name: build
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh    # keep the script out-of-line under .pipelines/scripts/

processingSteps:             # run sequentially (order = list position)
  - name: deploy
    configuration:
      image: alpine:latest
      script: |
        echo "deploying $VERSION"
```

You can inline the script with `script: |` or extract it to `.pipelines/scripts/build.sh` and
reference it with `scriptFile:` (the two are mutually exclusive). See the
[`config.yaml` Reference](config-reference.md) for every field and the extraction rules.

> **Don't declare a deploy trigger.** Binding configures the deploy poll for you — pushing a
> commit deploys automatically. The `triggers:` block is optional and additive; you only need
> it for extra triggers (e.g. polling an upstream version). See
> [Triggers](config-reference.md#triggers).

## Step 2 — Create a pipeline bound to your repo

One command creates the pipeline and binds it. If the bind fails, the draft pipeline is rolled
back, so you never get an orphan.

```sh
pl binding create you/my-app --credentials-secret GITHUB_TOKEN
```

- `--branch <name>` — branch to track (default `main`)
- `--path <dir>` — config directory in the repo (default `.pipelines`)
- `--credentials-secret <key>` — key in the pipeline's k8s secret holding the repo read token
  (omit for a public repo)
- `--trigger <mode>` — deploy trigger: `webhook` (default), `webhook-only`, or `poll`

The command prints the binding, including the pipeline **id** — you need it for the next steps.
(Or look it up any time with `pl pipeline list`.)

> `pl binding create` is the only way to create a pipeline: every pipeline is bound to a repo
> from birth, so its shape — including its **name** — always comes from git. The bind seeds a
> provisional name from the repo; the first reconcile sets it from `config.yaml`'s `name`.
> See [Binding & Reconcile](binding-and-reconcile.md).

## Step 3 — Set the secret values

Your config declares secrets **by name only**. Their *values* live in the pipeline's own
Kubernetes secret (`olve-pipeline-{id}`) and are mounted into every job — they never pass
through the repo or the app at runtime. Set each declared secret's value with `pl secret set`,
which reads the value from **stdin** by default so it stays out of your shell history and the
process list:

```sh
# from stdin (piped)
echo -n "ghp_xxxxxxxxxxxxxxxxxxxx" | pl secret set <pipelineId> GITHUB_TOKEN
# from an env var
pl secret set <pipelineId> GITHUB_TOKEN --from-env GITHUB_TOKEN
# from a file, verbatim (multi-line PEM keys round-trip intact)
pl secret set <pipelineId> SSH_PRIVATE_KEY --from-file ./id_ed25519
```

This is an **operational** command, so it works even though the pipeline is git-bound. Setting
values is the one piece of config that *can't* live in the repo, by design. (List which secrets
are set with `pl secret list <pipelineId>`.)

## Step 4 — Push, and watch the reconcile

Push a commit. Within ~5 minutes the server notices the branch moved, fetches your
`.pipelines/` subtree, compiles + validates the config, reconciles the live pipeline to match,
and runs the build. Check the result:

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

- **Reconcile `Success`** with no problems → your config applied and the build ran.
- **Reconcile `Error`** → live state is **unchanged**; read the listed problems and fix the
  config. See [Troubleshooting](troubleshooting.md).
- A secret shown as **`unset`** → set its value (Step 3). **`unknown`** means k8s couldn't be
  read, not that the secret is missing.

Don't want to wait for the poll? Apply the bound config immediately with
`pl binding reconcile <pipelineId>`.

## What happens on every subsequent push

1. The deploy poll sees the branch head move.
2. If `.pipelines/` changed, reconcile re-materializes steps/triggers to match the file.
3. The build runs; the ArtifactBundle flows through your processing steps in order.

A push that **only** touches code (not `.pipelines/`) still triggers a build — but skips the
reconcile (nothing to re-materialize). A **broken** config holds off the build for that cycle,
so bad config never ships on stale code. See
[config-before-build](binding-and-reconcile.md#config-before-build).

## Next

- Tune the file → **[`config.yaml` Reference](config-reference.md)**
- Understand the loop → **[Binding & Reconcile](binding-and-reconcile.md)**
- Pause/redrive a deploy → **[Promotion Gate](promotion-gate.md)**
- Something failed → **[Troubleshooting](troubleshooting.md)**
