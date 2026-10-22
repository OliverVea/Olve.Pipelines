# Getting Started — put a repo under CD

[← Index](index.md) · [Subject Index](subjects.md)

The end-to-end path from a repo with no CD to a pipeline that runs on every push. It is GitOps
only: you commit a `.pipelines/` directory and run one `pl binding create`.

## Before you start: the SDK is not published yet

A pipeline imports `@olve/pipelines`. The SDK is not on a package registry yet (SDK publishing is
deferred in the v2 design). The service's build Job does this, in order:

1. fetch the repo tarball at the commit;
2. if the repo has `sdk/package.json` **at its root**, run `bun install --frozen-lockfile --production` in `sdk/`;
3. in the binding path, run `bun install --frozen-lockfile`, then `bunx olve-build`.

So today the only layout that builds is the one Olve.Pipelines itself uses: an `sdk/` directory at
the repo root, and `"@olve/pipelines": "file:../sdk"` in `.pipelines/package.json`. Any other repo's
`bun install` fails in the build Job until the SDK is published. Everything below applies once
that is solved; until then it describes how Olve.Pipelines' own `.pipelines/` and
`.pipelines-test/` are set up.

## Prerequisites

- A GitHub repository the server can read: public, or private with a token.
- [Bun](https://bun.sh) on your machine, for `pl dev` and to create `bun.lock`.
- The v2 **`pl` CLI**. `install.sh` and `/download/pl-linux-x64` serve v1's `pl` from v1 prod; the v2
  self-deploy has no publish step yet. Build it from the `v2` branch:
  ```sh
  dotnet publish src/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/opt/pl-v2   # writes ~/.local/opt/pl-v2/pl (Native AOT)
  export PATH=~/.local/opt/pl-v2:$PATH                   # this shell: the v2 pl before a v1 pl on PATH
  ```
- A session against the v2 instance. `pl`'s default URL is v1 prod, and `~/.pl` holds a single
  session, so give v2 its own config file and name the instance:
  ```sh
  export PIPELINES_CONFIG=~/.pl-v2                        # keeps a v1 session in ~/.pl intact
  pl login --api-url https://pipelines-v2-beta.ovea.pro   # add --device over SSH
  ```
  `pl login` caches the token and the URL in that file. `PIPELINES_API_URL` and `--api-url` override
  the URL per command.

## Step 1 — Add `.pipelines/` to your repo

```
.pipelines/
  package.json          dependencies: @olve/pipelines; devDependencies: typescript, @types/bun
  bun.lock              committed: the build Job runs `bun install --frozen-lockfile`
  tsconfig.json
  models.ts             data models          (contract file)
  pipeline.ts           steps + pipeline     (contract file)
  handlers/<step>.ts    one per custom step  (not needed for library steps)
  scripts/*.sh          embedded into the compiled binary
```

`package.json` (as in this repo's `.pipelines/`):

```json
{
  "name": "my-app-pipeline",
  "private": true,
  "type": "module",
  "scripts": { "typecheck": "tsc --noEmit -p .", "describe": "olve-dev describe .", "build": "olve-build . --out .olve/out" },
  "dependencies": { "@olve/pipelines": "file:../sdk" },
  "devDependencies": { "@types/bun": "1.4.3", "typescript": "7.0.2" }
}
```

Copy `tsconfig.json` from [`.pipelines/tsconfig.json`](https://github.com/OliverVea/Olve.Pipelines/blob/v2/.pipelines/tsconfig.json).
It needs `"types": ["bun", "@olve/pipelines/assets"]` so that `import x from "./scripts/x.sh" with { type: "file" }`
type-checks, and it excludes `.olve` (the local work directory).

`models.ts`:

```ts
import { model, z } from "@olve/pipelines";

export const Request = model("Request", z.object({
  repo: z.string().default("you/my-app"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
}));
```

`pipeline.ts`:

```ts
import { pipeline, steps, Source } from "@olve/pipelines";
import testSh from "./scripts/test.sh" with { type: "file" };
import * as M from "./models";

export const source = steps.gitHubSource({ name: "source" });
export const test = steps.script({ name: "test", image: "alpine:3.22", script: testSh, input: { source: Source } });

export default pipeline({ name: "my-app", input: M.Request })
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(test);
```

`scripts/test.sh` gets its inputs as `INPUT_<KEY>` env vars and `$OLVE_INPUT` (see
[`steps.script`](pipeline-reference.md#library-steps)). Then:

```sh
cd .pipelines && bun install      # creates bun.lock — commit it
```

Add `.pipelines/.olve/` and `.pipelines/node_modules/` to `.gitignore`.

For a real build-and-deploy pipeline (kaniko image build, unit tests in parallel, Helm deploy over
SSH, health check), copy from [`.pipelines/pipeline.ts`](https://github.com/OliverVea/Olve.Pipelines/blob/v2/.pipelines/pipeline.ts).
Every option is in the [Pipeline Reference](pipeline-reference.md).

## Step 2 — Check it locally

```sh
pl dev describe            # manifest summary + describe-time checks; exit 78 on problems
pl dev validate            # also runs the service's reconcile checks (needs a session)
pl dev run source --input sha=$(git rev-parse HEAD)
pl dev run test            # chains: takes source's output from the local pool
```

`pl dev describe` for the example above prints:

```text
input:    Request { repo="you/my-app", sha* }
secrets:  none
triggers: push

stages:
  1.  source               alpine:3.22 -> Source
  2.  test                 alpine:3.22
```

See [Local Development](local-development.md) for the whole local loop.

## Step 3 — Create a pipeline bound to your repo

```sh
pl binding create you/my-app --trigger poll
```

- `--branch <name>` — branch to track (default `main`).
- `--path <dir>` — pipeline directory in the repo (default `.pipelines`). Several pipelines in one
  repo are several bindings with different paths (this repo binds `.pipelines` and `.pipelines-test`).
- `--trigger <mode>` — `webhook` (default), `webhook-only` or `poll`. **Use `poll` on v2 beta**: it
  has no public webhook route (`Webhooks:PublicBaseUrl` is empty), so a webhook can't be registered.
  See [deploy-trigger mode](binding-and-reconcile.md#deploy-trigger-mode).
- `--credentials-secret <key>` — key in the pipeline's secret that holds a GitHub token. Leave it out
  for a public repo. For a private repo, bind without it, set the secret (Step 4), then name it with
  `pl binding set-credentials <pipelineId> GITHUB_TOKEN`. Until the key holds a value, fetches run
  without a token, which fails for a private repo.

The command prints the binding, including the **pipeline id**. `pl secret` and `pl binding` take that
id. The run commands (`pl trigger`, `pl run`, `pl step`, `pl gate`, `pl pipeline …`) also accept the
pipeline's **name**, which comes from `pipeline({ name })` after the first reconcile. Quote names
that contain spaces.

## Step 4 — Set the secret values

A pipeline declares secrets by name (`secret("SSH_PRIVATE_KEY", "…")`). Their values live in the
pipeline's own Kubernetes Secret (`olve-pipeline-<id>`, in the runner namespace), never in the repo.
A step gets only the secrets it declares, as env vars of the same name.

`pl secret set` reads the value from stdin, so it stays out of shell history and the process list,
and sends it verbatim:

```sh
pl secret set <pipelineId> GITHUB_TOKEN --from-env GITHUB_TOKEN
pl secret set <pipelineId> SSH_PRIVATE_KEY --from-file ./id_ed25519
some-command-printing-the-value | pl secret set <pipelineId> API_KEY
```

Don't pass a value through `$(…)`: command substitution strips trailing newlines, which breaks SSH
keys. `pl secret list <pipelineId>` lists the names that are set; `pl binding status <pipelineId>`
shows which declared secrets are set.

A value another pipeline on the same instance already has (a shared `GITHUB_TOKEN`, say) can be
copied instead of set again. The service copies it; the value never reaches your machine:

```sh
pl secret copy "Olve.Pipelines v2"/GITHUB_TOKEN <pipelineId>            # one secret
pl secret copy <sourcePipeline>/GITHUB_TOKEN <pipelineId> --name GH_TOKEN  # under another name
pl secret copy <sourcePipeline> <pipelineId>                            # every secret of the source
```

Pipelines go by id or name. A name the target already has is refused unless you pass `--overwrite`.
Each copy is logged with who copied which names between which pipelines; values are never logged.
Keep in mind that anyone who can change the target pipeline can read a copied value from its steps.

## Step 5 — Reconcile, then run

The deploy poll checks the branch every 15 minutes. Don't wait — reconcile now:

```sh
pl binding reconcile <pipelineId>     # returns at once; the build Job takes a minute or so
pl binding status <pipelineId>
```

```text
Pipeline:  <id>
Repo:      you/my-app@main (.pipelines)
Reconcile: Success (last sync 2026-10-21 12:00:00Z)
Deployed:  <sha>
Synced:    <sha>
Secrets:
  (none declared)
```

- **`Success`** — the pipeline compiled, passed every check and is now a version:
  `pl pipeline versions my-app` lists it, `pl pipeline manifest my-app` summarizes it.
- **`Error`** — read the problems. Nothing changed: no version was added and the active version
  keeps running. Fix the pipeline and push (or `pl binding reconcile` again). See
  [Troubleshooting](troubleshooting.md).

The first time the service sees the branch head it only records it; it doesn't start a run. Start
the first run by hand:

```sh
pl trigger my-app --field sha=$(git rev-parse HEAD)     # prints my-app#1
pl run get my-app#1
pl step logs my-app#1/test --follow
```

## What happens on every push

1. The deploy poll (or a webhook) sees the branch head move.
2. If the push touched the binding path (or a local `file:` dependency such as `../sdk`), the
   service reconciles that commit first: build Job →
   checks → a new version if the manifest changed → `switch` to it. A run never starts on a
   definition older than its commit.
3. The pipeline's `onPush` mapper turns the push into a run input (in a short `map-trigger` Job),
   and a run starts on the active version. A newer automatic run **supersedes** an older running one.

Only commits that touch the binding path (or a local `file:` dependency of its `package.json`)
create versions. A pipeline without `onPush` ignores pushes.

## Next

- Write the real pipeline → **[Pipeline Reference](pipeline-reference.md)**
- Iterate on a step without pushing → **[Local Development](local-development.md)**
- Operate runs: rerun, continue, cancel, files, logs → **[Runs & Operations](runs-and-operations.md)**
- Hold deploys at a step → **[Promotion Gates](promotion-gate.md)**
- Something failed → **[Troubleshooting](troubleshooting.md)**
