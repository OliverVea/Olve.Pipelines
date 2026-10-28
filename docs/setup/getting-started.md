# Getting Started — put a repo under CD

[← Index](index.md) · [Subject Index](subjects.md)

The end-to-end path from a repo with no CD to a pipeline that runs on every push. It is GitOps
only: you commit a `.pipelines/` directory and run one `pl binding create`.

## The SDK

A pipeline imports the SDK, published to npm as
[`@olivervea/pipelines`](https://www.npmjs.com/package/@olivervea/pipelines):

```json
"dependencies": { "@olivervea/pipelines": "2.0.0-beta.19" }
```

Until v2 reaches prod, releases are `2.0.0-beta.N` on the `beta` dist-tag; pin an exact version.

The service's build Job fetches the repo tarball at the commit, runs `bun install --frozen-lockfile`
in the binding path, then `bunx olve-build`. A local
`file:`/`link:` dependency inside the repo (Olve.Pipelines' own `.pipelines/` uses
`"@olivervea/pipelines": "file:../src/sdk"`) is installed with `bun install --frozen-lockfile --production`
first.

## Prerequisites

- A GitHub repository the server can read: public, or private with a token.
- [Bun](https://bun.sh) on your machine, for `pl dev` and to create `bun.lock`.
- The **`pl` CLI**. Each instance serves the `pl` it was deployed with, set up to talk to that
  instance (installs to `~/.local/bin`; `PL_INSTALL_DIR` overrides):
  ```sh
  curl -fsSL https://pipelines-private.ovea.pro/install.sh | sh        # Linux
  irm https://pipelines-private.ovea.pro/install.ps1 | iex             # Windows
  pl --version
  ```
  From source: `dotnet publish src/backend/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/bin`.
- A session. `pl login` stores the instance URL with the token in a **profile** of `~/.pl`
  (`default` unless you pass `--profile`), so name the instance once:
  ```sh
  pl login --api-url https://pipelines-private.ovea.pro                  # profile 'default'; add --device over SSH
  pl login --profile beta --api-url https://pipelines-beta.ovea.pro      # a second instance beside it
  pl pipeline list --profile beta                                          # or PIPELINES_PROFILE=beta
  pl profile list
  ```
  `PIPELINES_API_URL` and `--api-url` override the URL per command.

## Step 1 — Add `.pipelines/` to your repo

```
.pipelines/
  package.json          dependencies: @olivervea/pipelines; devDependencies: typescript, @types/bun
  bun.lock              committed: the build Job runs `bun install --frozen-lockfile`
  tsconfig.json
  models.ts             data models          (contract file)
  pipeline.ts           steps + pipeline     (contract file)
  handlers/*.ts         one per custom step: implement(step, fn)  (library steps carry their own)
  scripts/*.sh          embedded into the compiled binary
```

`package.json`:

```json
{
  "name": "my-app-pipeline",
  "private": true,
  "type": "module",
  "scripts": { "typecheck": "tsc --noEmit -p .", "describe": "olve-dev describe .", "build": "olve-build . --out .olve/out" },
  "dependencies": { "@olivervea/pipelines": "2.0.0-beta.19" },
  "devDependencies": { "@types/bun": "1.4.3", "typescript": "7.0.2" }
}
```

Copy `tsconfig.json` from [`.pipelines/tsconfig.json`](https://github.com/OliverVea/Olve.Pipelines/blob/main/.pipelines/tsconfig.json).
It needs `"types": ["bun", "@olivervea/pipelines/assets"]` so that `import x from "./scripts/x.sh" with { type: "file" }`
type-checks, and it excludes `.olve` (the local work directory).

`models.ts`:

```ts
import { model, z } from "@olivervea/pipelines";

export const Request = model("Request", z.object({
  repo: z.string().default("you/my-app"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
}));
```

`pipeline.ts`:

```ts
import { pipeline, step, steps, Source } from "@olivervea/pipelines";
import * as M from "./models";

export const source = steps.gitHubSource({ name: "source", description: "Fetches the pushed commit." });
export const test = step({ name: "test", description: "Runs the tests.", image: "alpine:3.22", input: { source: Source } });

export default pipeline({ name: "my-app", input: M.Request })
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(test);
```

`handlers/test.ts` runs the repository's tests in the fetched source tree:

```ts
import { implement } from "@olivervea/pipelines";
import { test } from "../pipeline";
import testSh from "../scripts/test.sh" with { type: "file" };

export default implement(test, async ({ source }, ctx) => {
  await ctx.sh({ script: testSh }, { cwd: await ctx.files.fetch(source.tree) });
});
```

Then:

```sh
cd .pipelines && bun install      # installs the SDK from npm and creates bun.lock — commit it
```

Add `.pipelines/.olve/` and `.pipelines/node_modules/` to `.gitignore`.

For a real build-and-deploy pipeline (kaniko image build, unit tests in parallel, Helm deploy over
SSH, the API test suite against beta, a read-only smoke test of prod), copy from
[Olve.Template.Api's `.pipelines/`](https://github.com/OliverVea/Olve.Template.Api/tree/main/.pipelines),
the reference app pipeline (see [Starting from the reference pipeline](#starting-from-the-reference-pipeline)).
Every option is in the [Pipeline Reference](pipeline-reference.md).

## Step 2 — Check it locally

```sh
pl dev describe            # manifest summary + describe-time checks; exit 78 on problems
pl dev validate            # also runs the service's reconcile checks (needs a session)
pl dev run source --field sha=$(git rev-parse HEAD)
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
pl binding create you/my-app
```

- `--branch <name>` — branch to track (default `main`).
- `--path <dir>` — pipeline directory in the repo (default `.pipelines`). Several pipelines in one
  repo are several bindings with different paths (this repo binds `.pipelines` and `.pipelines-test`).
- `--credentials-secret <key>` — key in the pipeline's secret that holds a GitHub token. Leave it out
  for a public repo. For a private repo, bind without it, set the secret (Step 4), then name it with
  `pl binding set-credentials <pipelineId> GITHUB_TOKEN`. Until the key holds a value, fetches run
  without a token, which fails for a private repo.

The command prints the binding, including the **pipeline id**. Every command that takes a pipeline
accepts that id or the pipeline's **name**, which comes from `pipeline({ name })` after the first
reconcile. Quote names
that contain spaces.

## Step 4 — Set the secret values

A pipeline declares secrets by name (`secret("SSH_PRIVATE_KEY", "…")`). Their values live in the
pipeline's own Kubernetes Secret (`olve-pipeline-<id>`, in the runner namespace), never in the repo.
A step gets only the secrets it declares, as env vars of the same name.

`pl secret set` reads the value from stdin, so it stays out of shell history and the process list,
and sends it verbatim:

```sh
pl secret set <pipeline> GITHUB_TOKEN --from-env GITHUB_TOKEN
pl secret set <pipeline> SSH_PRIVATE_KEY --from-file ./id_ed25519
some-command-printing-the-value | pl secret set <pipeline> API_KEY
```

Don't pass a value through `$(…)`: command substitution strips trailing newlines, which breaks SSH
keys. `pl secret list <pipeline>` lists every secret the active version declares (and which step
uses it) or that is set, and whether it is set. A secret the config can point to (`{ secret: NAME }`)
but currently doesn't, or one only a branch the config doesn't select declares, shows as `not needed`
instead of `unset`.

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
Pipeline:    <id>
Repo:        you/my-app@main (.pipelines)
Reconcile:   Success (last sync 2026-10-21 12:00:00Z)
  commit:    <sha>
  version:   v1
Active:      active v1
Last push:   <sha>
Reconciled:  <sha>
```

`pl secret list my-app` shows which declared secrets are set.

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
2. If the push touched the binding path (or a local `file:` dependency such as `../src/sdk`), the
   service reconciles that commit first: build Job →
   checks → a new version if the manifest changed → `switch` to it. A run never starts on a
   definition older than its commit.
3. The pipeline's `onPush` mapper turns the push into a run input (in a short `map-trigger` Job),
   and a run starts on the active version. Older runs keep going; runs meet only at a step (see
   [Scheduling rules](runs-and-operations.md#scheduling-rules)).

Only commits that touch the binding path (or a local `file:` dependency of its `package.json`)
create versions. A pipeline without `onPush` ignores pushes.

## Starting from the reference pipeline

Copy the reference app pipeline,
[Olve.Template.Api's `.pipelines/`](https://github.com/OliverVea/Olve.Template.Api/tree/main/.pipelines):
source → build ∥ test → deploy-beta → beta-test → deploy-prod → prod-test.
`beta-test` runs the app's whole API test suite against the fresh beta (`mise run api:remote` in the
dotnet SDK image, the in-cluster Service as `API_BASE_URL`), authenticated with a token it fetches
from a beta-only Authentik machine client (client credentials; the client secret is a pipeline
secret declared on `beta-test` only); `prod-test` is a read-only smoke test
(`handlers/lib/smoke.ts`), since the suite writes data. Change the repo, release, namespaces, chart
values, the test command and the smoke checks.
A repo without a beta environment gets one all the same (homelab-dashboard: the release in
`apps-beta` with its ingress turned off by `set`), and a repo without a test suite gets checks that
prove the deployment serves this commit (homelab-dashboard compares the served files with the
source tree). An image over a few hundred MB can't travel as `imageBuild`'s tarball (the step
pod's scratch space is 1Gi): push it to `registry.ovea.pro` from a custom kaniko step and pull it on
the node (QuestionBank's `build-figures` and `deploy-figures-*`). Then:

1. Check the pipeline locally (`pl dev describe`, `pl dev validate`).
2. Push `.pipelines/`, then `pl binding create <owner/repo> --branch <deploy branch>
   --credentials-secret GITHUB_TOKEN` (the token key also for public repos: the anonymous GitHub
   rate limit is shared by every binding; `pl binding set-credentials` changes it later). The first
   reconcile builds it and only records the branch head.
3. Set the secrets the steps declare, piping bytes, never through `$(…)`:
   `pl secret set <pipeline> SSH_PRIVATE_KEY < key`.
4. Push the next commit. That push starts the first run; `pl wait push <pipeline> <sha>` waits for
   it.

Only one system should deploy a repo at a time: if something else deploys it today, stop that
before step 2.

## Next

- Write the real pipeline → **[Pipeline Reference](pipeline-reference.md)**
- Iterate on a step without pushing → **[Local Development](local-development.md)**
- Operate runs: rerun, continue, cancel, files, logs → **[Runs & Operations](runs-and-operations.md)**
- Hold a pipeline at a step while you work on it → **[Blocking Steps](blocking-steps.md)**
- Something failed → **[Troubleshooting](troubleshooting.md)**
