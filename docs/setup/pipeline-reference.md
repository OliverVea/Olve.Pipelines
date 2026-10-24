# Pipeline Reference (`pipeline.ts`)

[← Index](index.md) · [Subject Index](subjects.md)

How to write a v2 pipeline. The SDK's own reference —
[`sdk/README.md`](https://github.com/OliverVea/Olve.Pipelines/blob/v2/sdk/README.md) — has every
`ctx` helper, the harness contract and the manifest format; this page covers what you need to author
a pipeline.

## Layout

The binding path (`.pipelines/` by default) is a Bun package:

```
models.ts                 data models                           (contract file)
pipeline.ts               steps + `export default pipeline(…)`  (contract file)
handlers/*.ts             one per custom step: `export default implement(step, fn)`
handlers/lib/…            shared helpers (not handlers)
scripts/*.sh              embedded: import s from "../scripts/x.sh" with { type: "file" }
package.json, bun.lock, tsconfig.json
```

- **Contract files** hold contracts only, never handler code. The service evaluates them alone to
  produce the manifest ("describe"), so they must be deterministic: no env, time or network.
- **A handler names its step**: each top-level `handlers/*.ts` default-exports `implement(step, fn)`,
  and that `step` reference is what pairs them; the file name is free (`handlers/deploy-prod.ts` by
  habit). A custom step no handler implements, two handlers for one step, or a top-level file that
  isn't a handler is a describe error; library steps need none. Shared code goes in a subfolder.
- **Scripts are embedded** into the compiled binary. The repo is *not* in the step container — a
  step that needs the source gets it as a file reference (`Source.tree`).
- Dependencies point one way: `models ← steps ← handlers`, `models ← pipeline`.

## Models

```ts
import { model, z, fileRef } from "@olivervea/pipelines";

export const BuildArtifact = model("BuildArtifact", z.object({
  version: z.string(),
  image: fileRef(),     // a file in some step's output
  chart: fileRef(),     // a directory (its path ends with "/")
}));
```

- **Nominal**: `BetaDeployment` and `ProdDeployment` with the same shape are not interchangeable.
- **One schema per model name.** Two different schemas under one name is a describe error.
- **Outputs are validated strictly**: unknown keys are rejected at any depth, not stripped. An output
  is capped at 64 KB of JSON — put large data in files.
- **Files are references** (`fileRef()`), not inline data: `{ run, step, attempt, path, sha256 }`.
  A later step downloads one with `ctx.files.fetch(ref)`, which verifies the hash.
- **Two views**: trigger mappers and manual input produce `z.input` (fields with defaults may be
  left out); handlers, mapper refs and `when` see `z.output` (defaults applied).
- SDK models shared by the library steps: `Source { repo, sha, tree }`, `BuiltImage { repository,
  tag, digest, tar }` (an image tarball), `PushedImage { repository, tag, digest }` (an image in a
  registry), `HelmRelease { release, namespace, image }`, `FailureContext`.

## Secrets

```ts
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for the homelab host.");
```

Declared by name and description only. Values are set with `pl secret set <pipelineId> <NAME>` and
live in the pipeline's Kubernetes Secret. **A step gets only the secrets it declares**, as env vars
of the same name; `ctx.secrets` is typed to exactly that set.

## Steps

```ts
export const deployBeta = step({
  name: "deploy-beta",                  // identity: lower-case kebab, ≤ 63 chars; not "input"/"failure"
  description: "Deploys to beta.",      // required (may be ""); shown in the UI
  image: "alpine:3.22",                 // any x86-64 image — the harness brings its own runtime
  input: { artifact: M.BuildArtifact }, // model | optional(model) | plain zod schema
  output: M.BetaDeployment,             // a model, or omitted (no output)
  secrets: [SSH_PRIVATE_KEY],
  timeoutSeconds: 900,                  // optional; default 3600, capped at 21600
  retries: 1,                           // optional: retry a failed attempt (fresh pod) up to n times
});
```

- The **name is the step's identity**: history, scheduling, gates and addresses are keyed on it.
  Renaming a step starts a new history.
- The **description** is required but may be empty. It is display-only: changing it makes a new
  pipeline version but does not bump the step's version.
- An input entry that is a **plain schema** (`{ repo: z.string() }`) has no model to match on, so the
  stage must give the step a mapper.
- The image needs a shell only if the handler runs shell commands (`ctx.sh`); `ctx.exec` works on
  distroless images.
- **`retries: n`** is an idempotency claim: a failed attempt is retried automatically, as a new
  attempt in a fresh pod, until the step has failed 1 + n times in a row. Cancelled and obsolete
  attempts and scheduler-side failures are never retried; a continue resets the count. Without it,
  nothing is retried automatically.

## Library steps

SDK steps with a built-in handler and typed config. The set grows from real use.

| Step | Image | Input → output | Config |
|---|---|---|---|
| `steps.script` | yours | yours | `script` (embedded), `env` (values may be functions of the inputs) |
| `steps.gitHubSource` | `alpine:3.22` | `{ repo: "owner/name", sha }` (plain schema: **needs a mapper**) → `Source` | `token?: Secret` (private repos; raises the rate limit) |
| `steps.imageBuild` | `gcr.io/kaniko-project/executor:debug` | `{ source: Source }` → `BuiltImage`, or `PushedImage` with `push` | `repository`, `context?` (dir in the source), `dockerfile?` (relative to the context), `push?`, `contentTag?` (with `push`), `cacheRepo?` (kaniko layer cache), `registryMap?` (base-image mirrors; defaults to the pull-through caches), `stallTimeoutSeconds?` (default 300, 0 = off), `retries?`, `registryAuth?` (`{ username, password: Secret }`), `insecureRegistry?` |
| `steps.helmDeployOverSsh` | `alpine:3.22` | `{ source: Source, image: BuiltImage }` (per `images`; `PushedImage` with `fromRegistry`) → `HelmRelease` | `host` (`user@host`), `sshKey`, `release`, `namespace`, `chart` (dir in the source), `values?`, `set?`, `images?`, `fromRegistry?`, `imageRefs?`, `deployments?`, `rolloutTimeoutSeconds?`, `healthUrl?` |

All take `name`, `description` and `timeoutSeconds?`.

- **`steps.script`** — the zero-TypeScript path. The script gets `$OLVE_INPUT` (the validated input
  as JSON) and one `INPUT_<KEY>` env var per input key (`deployTo` → `INPUT_DEPLOY_TO`; strings raw,
  everything else JSON). With an output model it writes the output JSON to `$OLVE_OUTPUT`;
  `{"$file": "<path>"}` / `{"$dir": "<path>"}` become file references.
- **`steps.imageBuild`** builds `<context>/<dockerfile>` (default: `Dockerfile` at the source root,
  with the root as context) with kaniko (`--no-push`) and outputs the image tarball. The tag is the run's version stamp (`2026.10.22.6`), plus
  `-attempt<n>` on reruns. A stall watchdog kills kaniko when it prints nothing for
  `stallTimeoutSeconds` (a base-image pull can hang at "Unpacking rootfs"), failing the attempt;
  with `retries` a fresh attempt follows. Base images come through the homelab pull-through caches
  by default (`mcr-cache.ovea.pro` for mcr.microsoft.com, `hub-cache.ovea.pro` for Docker Hub):
  under gVisor, kaniko's pulls from mcr.microsoft.com stall often; a nearby cache doesn't.
  `registryMap` (kaniko `--registry-map`) replaces that default; `{}` turns it off. Caches never
  block a build: an unreachable cache (or `cacheRepo` registry) is skipped with a log line and
  kaniko pulls upstream, and kaniko falls back upstream when a mirror lacks an image.
  **`push: true`** is for images too big to travel as a file (the step's scratch space is ~1 GiB):
  kaniko pushes to `repository` (which must name a registry host) and the output is a `PushedImage`.
  With **`contentTag: true`** the tag is `src-<hash>` of the build context's content, and the build is
  skipped when the registry already has that tag:

  ```ts
  export const buildFigures = steps.imageBuild({
    name: "build-figures", description: "…", repository: "registry.ovea.pro/questionbank/figure-service",
    context: "services/figure-service", push: true, contentTag: true,
  });
  ```
- **`steps.helmDeployOverSsh`** streams each image tar into the host's k3s containerd (with
  `fromRegistry`: pulls each `PushedImage` there), runs `helm upgrade --install` (your `values`, then
  `set`, then per image `<prefix>.repository`/`.tag`/`.pullPolicy`, which always win: `Never` for
  tarballs, `IfNotPresent` for registry images), waits for each rollout (`deployments`, default the
  release; `rolloutTimeoutSeconds`, default 120), and with `healthUrl` GETs it until 2xx (5 × 5 s, or
  `{ url, attempts, delaySeconds }`). `images` maps step input keys to values prefixes (default
  `{ image: "image" }`); two images of one chart:

  ```ts
  export const deployBeta = steps.helmDeployOverSsh({
    name: "deploy-beta", description: "…", host: "oliver@bulwark-m2", sshKey: SSH_PRIVATE_KEY,
    release: "olve-slimerepublics", namespace: "apps-beta", chart: "helm",
    images: { server: "image", web: "web.image" },
    deployments: ["olve-slimerepublics-server", "olve-slimerepublics-web"],
  });
  // two BuiltImage producers, so a mapper picks them:
  .stage([deployBeta, from => ({ source: from.source, server: from["build-server"], web: from["build-web"] })])
  ```
- **Helpers for custom handlers:** `httpHealthCheck(ctx, url, { attempts?, delaySeconds?, headers? })`;
  `serviceUrl(release)` (`http://<release>.<namespace>.svc.cluster.local` from a `HelmRelease`);
  `checkSpa(ctx, url)` (the page references an `/assets/*.js` bundle served as JavaScript; returns
  `{ html, bundle }`); `sshSession(ctx, { host, key })` (`run`, `text`, `copy`, `shell` over SSH,
  as `helmDeployOverSsh` uses it).

Exact behaviour: [sdk/README.md "Library steps"](https://github.com/OliverVea/Olve.Pipelines/blob/v2/sdk/README.md#library-steps).

## Handlers

```ts
// handlers/test.ts
import { implement } from "@olivervea/pipelines";
import { test } from "../pipeline";

export default implement(test, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);          // directory refs are extracted
  await ctx.sh("dotnet test", { cwd: tree });
});
```

A handler returns a valid output (validated strictly against the output model) or calls
`ctx.skip(reason)`. Throwing fails the attempt. The main `ctx` members:

| `ctx.` | |
|---|---|
| `sh(cmd \| script, { env, cwd, args })`, `sh.text(…)` | Run a command or an embedded script through the image's shell. Non-zero exit throws. |
| `exec([argv…], { env, cwd, idleTimeoutSeconds })` | Run a binary with no shell. `idleTimeoutSeconds` (also on `sh`) kills a command that prints nothing for that long. |
| `files.fetch(ref)` | Download a file reference (sha256-verified); returns a local path. |
| `output.file(path, { name })`, `output.dir(…)`, `output.write(name, content)` | Add a file to the step's output; returns the `FileRef` to put in the output. |
| `http.getJson`, `http.expectOk`, `http.fetch` | HTTP, bound to cancellation. |
| `retry({ attempts, delaySeconds }, fn)` | Retry a function. |
| `secrets.NAME`, `log(msg, fields)`, `run` (`{ pipeline, id, number, attempt, step, version }`), `tmpDir`, `signal` | |

Test handlers with `testHandler(step, input, { ctx })` from `@olivervea/pipelines/testing` under `bun test`.

## The pipeline

```ts
export default pipeline({ name: "my-app", description: "…", input: M.DeployRequest })
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .when({ deployTo: "beta" }, p => p.stage(deployBeta))
  .onFailure(notify, { steps: [deployBeta] });
```

`name` becomes the pipeline's name in `pl` and the UI.

### Stages

Stages run in order; the steps of one stage run in parallel. A stage is a **barrier**: the next stage
starts only when every step of this one succeeded or was skipped. That is what gates a deploy on the
tests — `build` and `test` in one stage, `deploy` in the next.

### Input matching and mappers

By default each step input is **matched by model** against what is available: the pipeline input
plus the outputs of earlier stages. Give a mapper as a `[step, mapper]` tuple when you need one:

- `.stage(a)`, `.stage(a, b)` — matched by model.
- `.stage([a, from => ({ … })])`, `.stage([a, mapA], [b, mapB])`, or tuples mixed with plain steps.
- `from` is keyed by step name, plus `input` for the pipeline input: `from.input.sha`,
  `from.build.image`, `from["deploy-beta"].url`.

A mapper is needed for plain-schema inputs (e.g. `gitHubSource`), when two earlier steps produce the
same model, or to fix constants. Mappers are recorded into the manifest as data, never run by the
service, so the body must be **an object literal of property accesses and literals** (module
constants, constant arrays and nested object literals are fine). Comparisons, `!`, `&&`, `||`, `??`,
ternaries, arithmetic, template strings, calls, spread and optional chaining are rejected.

### `when` blocks

```ts
.when({ deployTo: "prod" }, p => p.stage(deployProd))
.when({ deployTo: ["beta", "prod"] }, p => …)
```

A declarative match on **literal-like fields of the pipeline input** (enums, booleans, literals;
a value or an array of values). Conditions are data, so the UI shows a run plan before a run starts.
Blocks nest.

- A step whose `when` doesn't match is **not part of the run**: it gets no attempt and no output, and
  shows as "not in run". A later step that requires that output is skipped, with a chained reason.
- An output produced inside a `when` is only *maybe* available afterwards. A later step must declare
  it `optional(Model)` (the handler sees `T | undefined`), or it is a compile error. `optional` still
  needs an earlier producer of the model.

### Triggers

**Push.** `.onPush(e => input)` maps a push of the bound branch to the pipeline input (the model's
`z.input` view, type-checked). The event is `{ ref, after, repository: { full_name } }` (the SDK's
`PushEvent`): the branch ref, the new head sha and the `owner/repo` name. A pipeline calls `onPush` at most once; without it, pushes are ignored.
Pushes come from the binding (webhook or deploy poll), see
[Binding & Reconcile](binding-and-reconcile.md#how-changes-are-noticed).

**Poll.** `.onPoll(config, value => input)` GETs a URL on an interval, extracts a value by dot-path,
and starts a run when it changes:

```ts
export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "Read token for the commits API.");

.onPoll({ name: "upstream", url: "https://api.github.com/repos/o/r/commits/main",
          secrets: [GITHUB_TOKEN], headers: { Authorization: "Bearer $SECRET:GITHUB_TOKEN" },
          valuePath: "sha", intervalSeconds: 300 },
        value => ({ sha: value }))
```

- `name` defaults to `"poll"` and must be unique per pipeline.
- Header values may reference `$SECRET:NAME`; the name must be in the poll's own `secrets`
  (type-checked).
- The last value is persisted, so a change during a restart still triggers. A new trigger or a
  changed URL only records the first value.

**Webhook.** `.onWebhook(config, event => input | null)` starts a run from an HTTP delivery to
`<public base>/api/hooks/<pipeline id>/<name>`, authenticated by a declared secret:

```ts
export const RELEASE_HOOK = secret("RELEASE_HOOK", "HMAC key of the GitHub release webhook.");

.onWebhook({ name: "release", verify: githubSignature(RELEASE_HOOK) },
           ({ headers, body }) => headers["x-github-event"] === "release"
             ? { sha: (body as { release: { target_commitish: string } }).release.target_commitish }
             : null)
```

- `verify` is `hmacSha256({ secret, header, prefix? })`, `githubSignature(secret)` (HMAC in
  `X-Hub-Signature-256: sha256=…`) or `bearer(secret)` (`Authorization: Bearer …`). A bad signature
  is a 401; nothing is mapped.
- The event is `{ headers, body }`: header names lower-cased, without `authorization`, cookies and
  the signature header; the body as parsed JSON, or the raw text. Return `null` to ignore a delivery.
- The service answers `202` at once and maps in the background (a `map-trigger` Job), so senders with
  short timeouts (GitHub: 10 s) are fine. Runs started this way are automatic: they are de-duplicated
  like push and poll runs.
- At most 8 deliveries are mapped at once; beyond that the answer is `429` and the sender retries.
  A GitHub redelivery (same `X-GitHub-Delivery`) is accepted and ignored. A rotated secret is picked
  up within a minute.
- `name` is unique among the pipeline's triggers. Point the sender at the URL above and give it the
  secret's value (`pl secret set`). The path needs the instance's public ingress.

**Manual.** `pl trigger <pipeline> --field k=v` or the UI's trigger form (generated from the input
model). Input is validated before anything starts; invalid input is a 400, never a half-run.

Trigger mappers are code: each automatic event runs a short `map-trigger` Job on the compiled
binary, and the service re-validates the result.

### Failure handlers

```ts
export const notify = step({ name: "notify", description: "Posts the failure to a webhook.", image: "alpine:3.22", input: { failure: FailureContext }, secrets: [WEBHOOK_URL] });
// …
  .onFailure(notify, { steps: [deployBeta] })   // omit `steps` for any step
```

When a run fails, the service runs each handler whose scope contains a failed step, once per
failure, in the handler's image with its declared secrets. It is best-effort and untracked: not an
attempt of the run, never retried, invisible in the API/UI (its log is kept in storage under
`runs/<p>/<run>/_failure/`). `FailureContext` carries `pipeline {id, name}`, `run {id, number,
address, versionStamp, trigger, url}`, `version {number, commit}`, `input`, `reason` and
`failedSteps[] {step, attempt, address, reason, logUrl}` (only steps in the handler's scope). Links
use the instance's `Links:BaseUrl`.

## Checks that reject a pipeline

Enforced by TypeScript, by describe (`pl dev describe`, the build Job) and again by the service at
reconcile (`pl dev validate`). A pipeline that fails any check never becomes a version.

| Check | Caught by |
|---|---|
| Input model or mapped ref not available at that stage (including same-stage siblings) | TS, reconcile |
| Two earlier producers of a model, no mapper (ambiguous) | TS, reconcile |
| A maybe-available (inside `when`) output used as a required input | TS, reconcile |
| `optional(M)` with no earlier producer of `M` | TS, reconcile |
| Plain-schema input without a mapper; mapper type mismatch or missing field | TS, reconcile |
| Mapper key the step doesn't declare | reconcile |
| Logic on refs (`===`, `+`, templates, calls; truthiness, `!`, `??`, `\|\|`, ternaries) | TS / describe source analysis |
| `when` on an unknown or non-literal field, or an invalid value | TS, reconcile |
| Undeclared secret; poll `$SECRET:` header not in the poll's `secrets` | TS, reconcile |
| Duplicate step name; reserved name | TS, describe, reconcile |
| Two schemas under one model name | describe |
| Custom step without a handler file; handler file without a step | describe |
| Output nothing consumes | reconcile **warning** only (fine for gate steps like `test`) |

## What makes a new version

The service hashes the manifest. Any change adds a version; steps and models also get their own
versions (`deploy-beta.3`, `BuildArtifact.2`), bumped only when *that* entity changes:

- a model: its JSON Schema;
- a step: image, input/output model versions, secrets, mappings, and its **handler hash** — the
  handler bundled on its own, tree-shaken, including embedded scripts and the code it imports;
- a lockfile, Bun or SDK change bumps every handler;
- stage placement and `when` wrapping are pipeline changes, not step changes.

Only commits that touch the binding path or one of its local package dependencies (`file:` /
`link:` entries in its `package.json`, e.g. `file:../sdk`) are built; other commits run on the
active version.
