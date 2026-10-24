# Olve.Pipelines

A small GitOps CD service for a homelab. Pipelines are defined in TypeScript in the repo they
deploy (`.pipelines/pipeline.ts`): typed **models**, **steps** that consume and produce them,
**handlers** that implement the steps, and a pipeline of sequential **stages** of parallel steps.
The service compiles each definition into a versioned binary and runs every step as a Kubernetes
Job, in any x86-64 image.

```ts
export default pipeline({ name: "my-app", input: M.DeployRequest })
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after, deployTo: "prod" }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)                                      // parallel; inputs matched by model
  .when({ deployTo: "prod" }, p => p.stage(deploy));
```

This repo deploys itself with it: [`.pipelines/`](.pipelines) builds every component once, deploys
beta, smoke-tests it, deploys prod and promotes the same artifacts.

## Install

Every instance serves the `pl` CLI it was deployed with:

```bash
curl -fsSL https://<instance>/install.sh | sh     # Linux, to ~/.local/bin/pl
irm https://<instance>/install.ps1 | iex          # Windows
pl login --api-url https://<instance>             # add --device over SSH
```

No instance yet? `docker compose up -d --build` runs a throwaway one at <http://localhost:5080>
([details](docs/setup/self-hosting.md#a-local-instance-with-docker-compose)); for a real one see
[Running an Instance](docs/setup/self-hosting.md).

## Adding CD to your repo

1. Add `.pipelines/` (`models.ts`, `pipeline.ts`, `handlers/`, `package.json`, `bun.lock`), written
   against the SDK `@olivervea/pipelines` ([sdk/](sdk/README.md)). See
   [Getting Started](docs/setup/getting-started.md) and the
   [Pipeline Reference](docs/setup/pipeline-reference.md).
2. Check it locally: `pl dev describe`, `pl dev validate`, `pl dev run <step>`.
3. `pl binding create <owner/repo>`, the only way to create a pipeline.
4. `printf '%s' "$VALUE" | pl secret set "<pipeline>" <NAME>` for each declared secret, and
   `pl config set "<pipeline>" -f app.yml` if it declares config.
5. Push. `pl binding status "<pipeline>"` shows the reconcile, and
   `pl wait push "<pipeline>" <sha> --until finished` follows the run.

The repo is the only writer of a pipeline's shape: no endpoint changes it. Operations (trigger,
cancel, rerun, continue, block a step, switch versions, secrets, config) go through `pl` or the web
UI.

## Core commands

```bash
pl pipeline list                                  # health, last run, active version
pl run list "<pipeline>"                          # newest first
pl run get "<pipeline>#<run>"                     # stages and each step's status
pl step logs "<pipeline>#<run>/<step>" --follow
pl trigger "<pipeline>" --field k=v               # a manual run
pl run continue "<pipeline>#<run>" --field k=v    # go on with changed input
pl step rerun "<pipeline>#<run>/<step>" --continue
pl wait run "<pipeline>#<run>"                    # exit 0 succeeded, 1 failed
```

Every command: [The `pl` CLI](docs/setup/cli.md), or `pl help`.

## Docs

Every instance serves the docs at `/docs` (raw Markdown; agents start at `/docs/llms.txt`).

- [Overview](docs/setup/index.md) · [Getting Started](docs/setup/getting-started.md) · [Subject Index](docs/setup/subjects.md)
- [Pipeline Reference](docs/setup/pipeline-reference.md) · [Pipeline Config](docs/setup/configuration.md) · [Local Development](docs/setup/local-development.md)
- [Runs & Operations](docs/setup/runs-and-operations.md) · [Blocking Steps](docs/setup/blocking-steps.md) · [Troubleshooting](docs/setup/troubleshooting.md)
- [The `pl` CLI](docs/setup/cli.md) · [API Reference](docs/setup/api-reference.md) (generated from `api.json`)
- [Running an Instance](docs/setup/self-hosting.md) · [Deploying to Docker Compose](docs/setup/compose-deploy.md)

## Repository

| Path | What |
|---|---|
| `src/Olve.Pipelines/` | The server: API, scheduler, Kubernetes executor (minimal API, Native AOT) |
| `src/Olve.Pipelines.Cli/` | `pl` |
| `sdk/` | `@olivervea/pipelines`: the authoring API, the harness, the build and dev tools (Bun) |
| `frontend/` | The web UI (Lit + Vite) |
| `helm/`, `docker-compose.yml`, `compose/` | The Helm chart; a local instance |
| `harness/` | The step pods' init image |
| `docs/setup/` | User docs (served at `/docs`); `docs/design/`: specs and plans |

Working on Olve.Pipelines itself: [CONTRIBUTING.md](CONTRIBUTING.md).
