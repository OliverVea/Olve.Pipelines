# Olve.Pipelines v2 — typed pipelines in TypeScript

The design of typed pipelines: how a pipeline is written, compiled, versioned and run. How to use it
is in [docs/setup](../setup/index.md) (the authoring API in [pipeline-reference.md](../setup/pipeline-reference.md)
and `src/sdk/README.md`); areas with a design of their own are [ci.md](ci.md),
[pipeline-config.md](pipeline-config.md), [external-inputs.md](external-inputs.md),
[matrix-builds.md](matrix-builds.md), [preview-environments.md](preview-environments.md) and
[spec-first.md](spec-first.md). What comes next is in [roadmap.md](roadmap.md).

## 1. Goals

1. **Typed data between steps.** Every step declares the models it consumes and the model it
   produces. Wiring mistakes are compile errors; bad outputs fail at the step that produced them.
2. **Pipeline behavior defined in TypeScript** as sequential stages of parallel steps. A step may
   still just call a shell script — the typing describes the data, not how it is produced.
3. **Introspectable.** The models, steps and wiring are plain data in a manifest, so the GUI and
   `pl` can render forms, run plans, typed inputs/outputs and history without running user code.

Not built: output caching and environments/tags (see [Open questions](#9-open-questions) and the
[roadmap](roadmap.md)).

## 2. Concepts at a glance

| Concept | What it is |
|---|---|
| **Model** | A named, nominal zod schema (`BuildArtifact`). Emitted as JSON Schema. |
| **Step** | A contract: name, image, inputs (models or plain schemas), output model, secrets. No behavior. |
| **Handler** | The implementation of one step: `(input, ctx) => output`, attached with `implement(step, fn)`. |
| **Library step** | An SDK-provided implemented step with typed settings (`gitHubSource`, `imageBuild`, `helmDeployOverSsh`, …). |
| **Pipeline** | Input model, triggers, and an ordered list of **stages**; each stage runs its steps in parallel. |
| **Run** | One execution of a pinned pipeline version: an input plus the outputs of each step (the **pool**). |
| **Attempt** | One execution of one step within a run. Reruns add attempts. |
| **Version** | Immutable snapshot. Pipelines, steps and models each have `<id>.<version>`. |

## 3. Authoring

### 3.1 Layout

```
.pipelines/              # binding path (overridable — several pipelines per repo = several bindings)
  package.json           # `main` names the entry point
  models.ts              # data models
  pipeline.ts            # the entry point: input, triggers, stages
  steps.ts               # custom steps, each with its implementation
  scripts/               # shell scripts, embedded into the binary at build
```

A pipeline is an ordinary package, so only `package.json` has a fixed name. Dependency direction:
`models ← steps ← pipeline`. Steps never depend on the pipeline; their types come from the step
definition alone.

- **Importing the entry point is deterministic**: describe evaluates it and everything it imports.
- **Steps carry their handlers**: `implement(step({ … }), fn)` is imported and staged like any value.
  `step()` alone is a contract; staging one without an implementation is a describe error.
- **Scripts are embedded**: `import script from "./scripts/x.sh" with { type: "file" }`. The repo
  is not in the step container; the harness copies embedded scripts out before running them.

### 3.2 Models

```ts
import { model, z, fileRef } from "@olivervea/pipelines";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.Pipelines"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  deployTo: z.enum(["beta", "prod"]).default("prod"),
  publishCli: z.boolean().default(true),
}));

export const BuildArtifact = model("BuildArtifact", z.object({
  version: z.string(),
  commit: z.object({ repo: z.string(), sha: z.string() }),
  image: fileRef(),          // a file in the step's bundle dir
  chart: fileRef(),          // a directory in the step's bundle dir
}));

export const BetaDeployment = model("BetaDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));
export const ProdDeployment = model("ProdDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));
```

- Models are **nominal**: `BetaDeployment` and `ProdDeployment` share a shape but are not
  interchangeable (`model()` brands with the name).
- **Files are references**, not inline data. Structured output stays small (64 KB cap); large
  data lives in the bundle and is referenced via `fileRef()`, marked `"x-olve-kind": "fileRef"` in
  the JSON Schema so the service and GUI recognize it.
- **One schema per model name** — two different schemas registered under one name is a describe error.
- **Outputs are validated strictly**: unknown keys are rejected, not silently stripped.
- **Input vs output view**: trigger mappers produce the model's `z.input` (fields with defaults are
  optional); handlers, mapper refs and `when` see `z.output` (defaults applied).
- zod over TypeSpec: the service and GUI only consume JSON Schema (zod v4 `z.toJSONSchema`). Because
  JSON Schema is the interchange format, another model source can be added later without touching
  the service.

### 3.3 Steps

```ts
import { step, secret, Source } from "@olivervea/pipelines";
import * as M from "./models";

export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for image import + helm on the host.");

export const build = step({     // custom: image build + helm package → BuildArtifact
  name: "build",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  output: M.BuildArtifact,
});

export const deployBeta = step({
  name: "deploy-beta",
  image: "alpine:3.24",
  input: { artifact: M.BuildArtifact },
  output: M.BetaDeployment,
  secrets: [SSH_PRIVATE_KEY],
  timeoutSeconds: 900,          // optional
});

export const deployProd = step({
  name: "deploy-prod",
  image: "alpine:3.24",
  input: { artifact: M.BuildArtifact },
  output: M.ProdDeployment,
  secrets: [SSH_PRIVATE_KEY],
});
```

- A step's **name is its identity** (history, scheduling and blocked steps are keyed on it).
- Each **input entry** is a model, `optional(model)` (see skip propagation, §3.6), or a plain zod
  schema (`{ repo: z.string() }`). Plain-schema entries have no model to match on, so the step
  **requires a mapper**.
- Steps declare the **secrets** they use; only those are mounted into the step's Job, and
  `ctx.secrets` is typed to exactly that set.
- `image` can be any image — the harness brings its own runtime (see [§5.1](#51-harness)).

### 3.4 Library steps

SDK-provided steps with typed settings and SDK models (`Source`, `BuiltImage`, `HelmRelease`, …), so
pipelines share vocabulary: `gitHubSource`, `imageBuild`, `helmDeployOverSsh`, `helmDeployInCluster`,
`composeDeploy` (their settings are in `src/sdk/README.md`).

```ts
export const source = steps.gitHubSource({ name: "source", description: "", token: GITHUB_TOKEN });
//   input { repo, sha } (plain schemas → mapper required) → output Source { repo, sha, tree: fileRef }
```

- **One mechanism.** `step()` takes a `kind` (default `custom`) and `settings`; a library step is a
  function returning `implement(step({ kind, settings, … }), handler)`, exactly what a pipeline
  writes. The harness, describe and `testHandler` dispatch on "does the step carry an
  implementation", never on its kind, and to the service a kind is only a label. So a step from
  another package works the same, and `tests/runtime/library-public.test.ts` keeps the library on the
  public API.
- **The library grows from use.** A step joins it when several pipelines need it; a single-use step
  stays custom in its pipeline.
- `settings` may reference config entries ([pipeline-config.md](pipeline-config.md)); describe
  records them as the step's manifest `config`. Values that depend on the run (a tag containing the
  version) are computed inside the handler, so describe stays pure.

### 3.5 Handlers

```ts
// steps.ts
import { implement, step } from "@olivervea/pipelines";
import deployScript from "./scripts/deploy-beta.sh" with { type: "file" };

const RELEASE = "olve-pipelines";
const URL = "https://pipelines-beta.ovea.pro";

export const deployBeta = implement(step({ name: "deploy-beta", /* … */ }), async ({ artifact }, ctx) => {
  await ctx.sh(deployScript, {
    env: { RELEASE, VERSION: artifact.version,
           IMAGE_TAR: await ctx.files.fetch(artifact.image),
           CHART_DIR: await ctx.files.fetch(artifact.chart) },
  });
  await ctx.retry({ attempts: 5, delaySeconds: 5 }, () => ctx.http.expectOk(`${URL}/api/health`));
  return { release: RELEASE, version: artifact.version, url: URL };
});
```

`ctx` surface: `sh`, `exec`, `readText`, `readJson`, `files.fetch`, `output.file`, `output.dir`,
`output.write`, `http`, `retry`, `secrets`, `config`, `log`, `signal` (cancellation/timeout), `run`
(run id, number, version stamp), `tmpDir`, `link(url, label?)`, `inputUrl` (external inputs),
`skip(reason)` (runtime escape hatch — prefer `when`).

**Step links.** `ctx.link` records where a step's work can be seen (a deployed app, a review). The
links travel in the result envelope on any status and are stored on the attempt; the server checks
them strictly (absolute http(s), at most 10, URL ≤ 2048 and label ≤ 100 characters). A step
declares no link of its own: the URL usually depends on config or on what the run did. A waiting
step gets its link from its external input or its preflight ([external-inputs.md](external-inputs.md)).

`ctx.sh` runs a command string or an embedded script (copied out to `/olve/tmp` first) via the
image's shell, detected from `/bin/sh`, `/busybox/sh`, `/bin/bash`; shell-less images (distroless)
get a clear error, not an `ENOENT`. Children use the image's own libc and tools. `idleTimeoutSeconds`
on `sh`/`exec` is an inactivity watchdog: a child that prints nothing for that long is terminated
(`CommandStalledError`), so a hung download fails the attempt instead of holding it until
`timeoutSeconds`.

Return type is `Infer<Output> | Skipped`: a handler produces a valid output or explicitly skips.

### 3.6 Pipeline

```ts
// source (§3.4), build, deployBeta, deployProd (§3.3) are defined above, each with its implementation;
// test, publishCli (a step consuming BuildArtifact) and aoeTriage (a failure handler) are defined elsewhere.
export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "Read-only token for polling the commits API.");

export default pipeline({
  name: "Olve.Pipelines",
  description: "Build, deploy beta then prod, publish the pl CLI.",
  input: M.DeployRequest,
})
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after,
                  deployTo: e.ref === "refs/heads/main" ? "prod" : "beta" }))
  .onPoll({ url: "https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/main",
            secrets: [GITHUB_TOKEN], headers: { Authorization: "$SECRET:GITHUB_TOKEN" },
            valuePath: "sha", intervalSeconds: 300 },
          value => ({ sha: value }))                   // z.input: repo/deployTo take their defaults
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)                                  // no mapper: match inputs by model (Source)
  .stage(deployBeta)                                   // BuildArtifact from build
  .when({ deployTo: "prod" }, p => p
    .stage(deployProd)
    .when({ publishCli: true }, p => p
      .stage(publishCli)))
  .onFailure(aoeTriage, { steps: [deployProd] });
```

**Stages.** Sequential; the steps within a stage run in parallel. A stage is a **barrier**: the next
stage starts only when every step of the current one has succeeded (or been skipped). This is what
gates deploys on tests — no declaration needed.

**Triggers.** Each trigger maps its event to the pipeline input model's `z.input` view (defaults
optional); the mapping is type-checked against it. Trigger mappers are **always code**: they run in
a short `map-trigger` Job from the compiled binary per event (§4), never proxy-recorded. Poll
triggers declare their own `secrets: [...]`, and `$SECRET:NAME` header references are type-checked
against that list. Manual triggers (GUI / `pl trigger --input deployTo=beta`) use a form generated
from the input model. Input is validated at trigger time; invalid input is a 400 (or a rejected
event), never a half-run.

- **Push** belongs to the binding, not the trigger: every binding is polled (an unchanged branch is
  a free 304) and gets a GitHub hook when the instance has a public base URL and the binding a token
  allowed to manage hooks. Whether a moved branch head starts a run is the manifest's `onPush`, so
  `onPush` takes no source config; repo, branch and credentials are binding data.
- **Webhook**: `onWebhook({ name, verify }, ({ headers, body }) => input | null)` receives
  `POST /api/hooks/{pipeline}/{trigger}`, verified with a declared secret (`hmac-sha256` over the raw
  body, or `bearer`), answered 202 and mapped in the background; `null` starts nothing.
- **Poll** remembers its last value per (pipeline, trigger) in `pipeline-state.json`, so a change
  made just before a restart still triggers; only a new trigger or a changed URL seeds.
- **Config before run.** On a bound pipeline every automatic trigger starts its run only once the
  binding's current config commit is applied (`IConfigLock`, reconciling first). A poll value is
  recorded only then, so a change seen while the config is unapplied is picked up by a later poll; a
  webhook delivery in that state is dropped and logged.

**Input mapping.** By default a step's inputs are matched **by model** from what is available (the
pipeline input + outputs of earlier stages). An optional per-step mapper builds the input
explicitly, given as a **`[step, mapper]` tuple**:

- `.stage(a)` / `.stage(a, b)` — no mappers, inputs matched by model.
- `.stage([a, from => ({ … })])` for a single mapped step; `.stage([a, mapA], [b, mapB])` for a
  parallel stage (tuples and plain steps can be mixed).
- `from` (the mapper parameter; `in` is a reserved word) is keyed by **step name**, plus `input` for
  the pipeline input.
- `from` holds typed **references** (`Ref<T>`, a `symbol & …` type), not values: only property
  access and literals compile; `===`, `+`, template strings and method calls on refs are type errors.
  Describe calls the mapper with recording proxies and stores a **tagged mapping** in the manifest
  (`{ "ref": "input.sha" }`, `{ "const": … }`, nested objects of these); the service builds
  `input.json` from it without running TypeScript, and the GUI draws exact field-level wiring.
- Proxies cannot see truthiness, `!`, `??`, `||` or ternaries on refs (they silently record a
  constant), so describe also **statically analyses the mapper source** (`fn.toString()`): the body
  must be an object literal of member chains and literals only. The harness is therefore never
  minified. Reconcile rejects mapper keys the step's input doesn't declare.
- Use a mapper to adapt library steps to your models, to feed plain-schema inputs (required), to
  resolve ambiguity (two producers of the same model), or to fix constants.

**Conditions.** `when` blocks are declarative field matches on **literal-like pipeline-input fields**
(enums, booleans, literals; value or array of values). Typed against the input model — `{ deployTo:
"prd" }` and `{ sha: "abc" }` are compile errors. Conditions are data in the manifest, so the GUI can
show a **run plan** before a run starts.

**Skip propagation.** A step whose `when` does not match is skipped and produces no output. Any later
step needing that output is skipped too, with a chained reason. Outputs produced inside a `when` are
only *maybe* available afterwards: a later step must declare them `optional(Model)` (handler sees
`T | undefined`), or it is a compile error. `optional` means *maybe skipped* only: there must still
be an earlier producer — `optional(M)` with no producer of `M` at all is an error.

**Failure handlers.** `.onFailure(handler, { steps? })` runs `handler` when a run fails. Its input
is a typed `FailureContext`: `pipeline {id, name}`, `run {id, number, address, versionStamp,
trigger, url}`, `version {number, commit}`, the run's `input`, a `reason` and `failedSteps[] {step,
attempt, address, reason, logUrl}` (only the steps in the handler's scope).

- A run going Running → Failed runs each handler of its *pinned* manifest whose scope (`steps`, none
  = any) holds one of its failed steps (each step's latest failed attempt since the run was last
  resumed). A run that failed without a failed step (unusable manifest) runs none.
- The context is projected onto the pinned manifest's `FailureContext` schema and validated first,
  so a version built with an older SDK model is skipped with a logged reason, not launched to fail.
- Untracked and best-effort: a harness Job `olve-fh-…` (role `on-failure`), named deterministically
  per run, failure and handler, so a duplicate dispatch reattaches and a continued run that fails
  again dispatches again. It gets the handler's declared secrets and uploads no result; its
  `input.json` and log go under `runs/<p>/<run>/_failure/<handler>/<id>/`. No attempt, no retry, no
  effect on the run, nothing in the API or UI. Links use `Links:BaseUrl`, else
  `Webhooks:PublicBaseUrl`.

### 3.7 Pipeline-build checks

Enforced in three layers: **TypeScript** (the builder accumulates available outputs in its type),
**describe** (checks only the evaluated source can answer) and **reconcile** (the service re-runs
every manifest-level check and rejects invalid configs — it never trusts that the compiler ran, and
script handlers bypass it anyway). The SDK's compile-fail checker holds every TS check below.

| Check | TS | Describe | Reconcile |
|---|---|---|---|
| Input model / mapped ref not available at this stage (incl. same-stage siblings) | ✓ | | ✓ |
| Two earlier producers of a model with no mapper (ambiguity) | ✓ | | ✓ |
| Maybe-available (inside `when`) output used as required input | ✓ | | ✓ |
| `optional(M)` with no earlier producer of `M` | ✓ | | ✓ |
| Plain-schema input without a mapper | ✓ | | ✓ |
| Mapper type mismatch / missing field | ✓ | | ✓ |
| Mapper key not in the step's input | — | | ✓ |
| Logic on refs: `===`, `+`, template strings, method calls | ✓ (`Ref<T>`) | ✓ (source analysis) | — |
| Logic on refs: truthiness, `!`, `??`, `\|\|`, ternaries | — | ✓ (source analysis) | — |
| `when` on unknown / non-literal field or invalid value | ✓ | | ✓ |
| Trigger mapping doesn't produce valid pipeline input | ✓ | | at event time (`map-trigger` output validated) |
| Step uses an undeclared secret; poll `$SECRET:` header not in the poll's `secrets` | ✓ | | ✓ |
| Duplicate step name | ✓ | ✓ | ✓ |
| Two schemas under one model name | — | ✓ | — |
| Staged step without an implementation | — | ✓ | — |
| Output nothing consumes | — | | warning (fine for gate steps like `test`) |

### 3.8 External inputs

A step input can come from outside the run (a person, a callback, a polled URL) instead of from the
pool: the step waits, with no pod, until the input is answered. Design:
[external-inputs.md](external-inputs.md).

## 4. Manifest

`describe` mode evaluates the entry point and what it imports (never running handlers) and emits:

```json
{
  "apiVersion": "2.0",
  "name": "Olve.Pipelines",
  "input": "DeployRequest",
  "harness": { "bun": "1.4.3", "sdkHash": "sha256:…" },
  "models": {
    "DeployRequest": { "input": { "type": "object", "properties": { "…": {} } },
                       "output": { "type": "object", "required": ["repo", "sha", "deployTo", "publishCli"], "…": {} } },
    "BuildArtifact": { "output": { "type": "object", "additionalProperties": false, "properties": {
                         "image": { "type": "object", "x-olve-kind": "fileRef" }, "…": {} } } }
  },
  "secrets": [ { "name": "SSH_PRIVATE_KEY", "description": "…" }, { "name": "GITHUB_TOKEN", "description": "…" } ],
  "triggers": [ { "kind": "push" },
                { "kind": "poll", "url": "…", "secrets": ["GITHUB_TOKEN"], "valuePath": "sha", "intervalSeconds": 300 } ],
  "stages": [
    { "steps": [ { "name": "source", "kind": "gitHubSource", "image": "…",
                   "input": { "repo": { "ref": "input.repo" }, "sha": { "ref": "input.sha" } },
                   "output": "Source" } ] },
    { "steps": [ "…build, test…" ] },
    { "steps": [ { "name": "deploy-beta", "image": "alpine:3.24",
                   "input": { "artifact": { "ref": "build" } }, "output": "BetaDeployment",
                   "secrets": ["SSH_PRIVATE_KEY"] } ] },
    { "when": { "deployTo": ["prod"] }, "stages": [ "…" ] }
  ],
  "failureHandlers": [ "…" ]
}
```

- **Mappings are tagged**: `{ "ref": "<step|input>.<path>" }`, `{ "const": <json> }`, or a nested
  object of these. Inputs matched by model are emitted resolved (`{ "ref": "build" }`), so the
  manifest is always explicit.
- **Two schema views per model**, emitted only where they differ: the **input** view (`z.input`)
  drives trigger forms and validation of manual input (the service fills `default`s from it); the
  **output** view (`z.output`, `additionalProperties: false`) validates step outputs, trigger
  results and the pool, and renders run history. Canonical JSON (sorted keys, no `$schema`).
- **Trigger mappers** (`onPush`, `onPoll`) are code and appear in the manifest only as the trigger's
  kind and config. Each event runs `map-trigger <trigger>` in a short Job; its result is parsed by
  the pipeline input model (yielding `z.output`) and re-validated by the service.

## 5. Runtime

### 5.1 Harness

The pipeline compiles (at reconcile, per commit) into **one standalone executable** (`bun build
--compile`, ~91 MB, ~38 MB gzipped) stored in Garage as `step.tar.gz`. It has four modes:

- `describe` → the manifest.
- `run <step>` → read `/olve/input.json`, validate against the step's input, call the handler,
  validate the return value strictly against the output model, write `/olve/work/output.json` (files
  under `/olve/work/files/`), pack both into `result.tar.gz` and PUT it to `OLVE_RESULT_URL`.
- `preflight <step>` → a step's preflight, before it waits for its external inputs
  ([external-inputs.md](external-inputs.md)).
- `map-trigger <trigger>` → short-lived helper mapping one event to pipeline input.

**`run` contract** (authoritative detail: `src/sdk/README.md`; manifest format: `src/sdk/manifest.schema.json`).
`output.json` is an envelope: `{ "status": "succeeded", "output": … }`, `{ "status": "skipped",
"reason": "…" }` or `{ "status": "failed", "error": { "kind": "handler"|"input"|"output", "message" } }`,
also printed last on stdout between `---OLVE-RESULT-BEGIN---`/`---OLVE-RESULT-END---` as a fallback.
Exit codes: `0` ok, `1` handler threw, `64` usage, `65` input invalid, `66` output invalid, `74` result
upload failed, `78` config invalid, `130`/`143` cancelled. The service provides `OLVE_RUN`,
`OLVE_ATTEMPT`, `OLVE_RUN_ID`, `OLVE_PIPELINE`, `OLVE_VERSION_STAMP`, declared secrets as same-named env
vars, `OLVE_RESULT_URL`, and `OLVE_FILE_URLS` (presigned GET per input file ref).

**Any image via a bundled loader.** The binary is the glibc `bun-linux-x64` build, shipped with
`/olve/lib` (ld-linux + libc/libpthread/libdl/libm from a pinned debian, ~3 MB) and always launched
through that loader explicitly:

```
/olve/lib/ld-linux-x86-64.so.2 --library-path /olve/lib /olve/step run <step>
```

This ran in debian-slim, dotnet/sdk, Alpine (musl), kaniko and distroless/static under gVisor
(+~10 ms). Rules: **never patchelf** (it segfaults Bun's embedded payload), **never set
`LD_LIBRARY_PATH`** (`--library-path` is per-process, so children use the image's own libc), and
re-exec self only via the loader command (`process.execPath` is the loader). A musl build with
bundled ld-musl + libstdc++ + libgcc_s is the documented fallback (immune to exotic NSS /
`ld.so.preload`; +~70 ms).

**Delivery.** A stable **`harness-base`** image (static busybox + `/olve/lib`, versioned with the
service) is the single init container. It fetches the per-version `step.tar.gz` and the per-attempt
`input.json` from Garage via presigned GETs into the `/olve` emptyDir. The step container runs the
loader command above with `TMPDIR=/olve/tmp` (kaniko has no `/tmp`); outputs go to `/olve/work`.

- **Secrets** are `secretKeyRef`s marked optional: a declared secret whose key is unset leaves its
  env var unset instead of failing the pod, so a handler can check for it and skip; `ctx.secrets.X`
  throws on access.
- **Collecting the result is memory-safe**: `HarnessResultCollector` streams `result.tar.gz` through
  a temp file and stages each file to disk before uploading it; only the capped `output.json` is
  held in memory, so an image tarball as a step output costs disk, not server memory.

**Build/describe Job** (on `oven/bun:<pinned>-slim` — never alpine, where `bun-linux-x64` silently
yields musl): fetch the repo tarball → `bun install --frozen-lockfile` in the pipeline and in each
local `file:`/`link:` dependency of its `package.json` (one resolving outside the repo fails the
build) → generate the entry point → `bun build --compile --target=bun-linux-x64 --no-compile-autoload-dotenv
--no-compile-autoload-bunfig --bytecode --format=esm` → verify the ELF `PT_INTERP` → PUT
`step.tar.gz` (presigned) → print the manifest between `---OLVE-MANIFEST-BEGIN---` /
`---OLVE-MANIFEST-END---` on stdout; the service reads it from the pod log (termination messages
cap at 4 KB). No callbacks to the API. ~1.3 s plus the first image pull.

The service never executes TypeScript itself.

### 5.2 Runs, pool and attempts

- A run **pins one pipeline version** and stores its input plus every step's output (the **pool**).
  Each job receives only the models its step declared (or its mapped paths), keeping contracts honest.
- The run's **version stamp is derived from the run** (`yyyy.MM.dd.<run number>`), so it is known
  before `build` starts.
- A step's outcome is `Succeeded`, `Failed`, `Skipped` (with reason), `Cancelled`, `Obsolete`. A failed step halts the run at its stage barrier.
- **Explicit reruns are attempts.** Rerunning a step adds an attempt in the same run with the same
  pinned version and pool input. In a failed or cancelled run the rerun continues the run; in a
  succeeded run only the step reruns (the API's `continue` flag; the UI and `pl` choose it). Later
  stages consume the **latest successful attempt**; earlier attempts stay in history. Rerunning a
  whole run creates a new run with the same input, linked to the original; that input is
  re-validated against the version the rerun binds to (defaults filled in).
- **Continue** a run with a changed input (e.g. `deployTo: beta → prod`) keeps completed stages and
  re-evaluates `when` for the rest. The changed fields are merged over the stored input and validated
  against the run's *pinned* input model; only fields no completed step consumed may change (e.g.
  `sha` cannot) — this is "promote what you tested in beta".
- **Cancel.** Cancelling a run also cancels its in-progress attempts (their Jobs are deleted), unlike
  being superseded; a finished run with still-active attempts gets only those cancelled. Cancelling a
  step cancels its active attempt; the run ends Cancelled at that stage's barrier, and rerunning the
  step with continue re-opens it.
- **No automatic retries** unless a step opts in with `retries: n` (an idempotency claim). A failed
  attempt is retried while the trailing streak of failed attempts since the run was last resumed is
  at most `n`; cancelled and obsolete attempts and failures on the scheduler's side (an oversized
  input) are never retried. A retry is a fresh attempt, so a fresh pod: `imageBuild` retries (2 by
  default) after its stall watchdog (300 s without output) kills kaniko, because kaniko unpacks the
  base image over the container's root and a second kaniko in the same container would snapshot a
  half-unpacked root as its base.
- **Logs** of a running attempt are its pod's current output; an attempt that never ran answers a
  one-line note, not an error.

### 5.3 Scheduling

- **Keyed on step name, not version.** At most one `InProgress` attempt per (pipeline, step);
  latest-wins among scheduled attempts (an older Scheduled attempt becomes Obsolete and its run is
  superseded).
- **Runs flow on concurrently.** Starting a run leaves older runs running; they meet only per step.
  A run never starts a step a newer run already started: it is superseded instead (linked to the
  newer run), so an older run can't deploy after a newer one, while under a stream of pushes an older
  run still reaches the end. In-progress attempts of a superseded run finish (a half-done deploy is
  never killed).
- **Trigger de-duplication applies to automatic triggers only** (push, poll, webhook): keyed on the
  canonical hash of the mapped input, against a Running run or the latest automatic run within an
  hour on the same version, so the same SHA from webhook and poll yields one run. Since mappers are
  code, de-dup needs the mapped input: one `map-trigger` Job per event, de-dup after it. Explicit
  actions (GUI, `pl`, API) always create work.
- **Blocked steps** are keyed on step name: a blocked step gets no attempt and holds its run at that
  step; unblocking resumes held runs. A block is an operator hold, operational state rather than
  config, so it survives version switches and reconcile. It is not an approval mechanism; *gate*
  means a step in the pipeline that decides whether a run goes on (tests, approval, visual review).

### 5.4 Persistence

Runs, pools, attempts, pipeline versions, activations, blocked steps and queued run starts are
persisted and survive a restart, including the instance redeploying itself mid-run: the executor
reattaches to its Jobs by name.

### 5.5 Addressing and storage

Past outputs and files are addressed by **run number** (monotonic per pipeline — the build number)
and **step name**, with an optional **attempt**:

```
<pipeline>#<run>/<step>[@<attempt>]

olve-pipelines#412/deploy-beta        → output of the latest successful attempt
olve-pipelines#412/deploy-beta@1      → a specific attempt (e.g. the failed one)
olve-pipelines#412/build/image.tar    → a file from that step's bundle
```

- The run pins the pipeline version (and so every step/model version and the input), so addresses
  never mention versions.
- The step name is the identity, stable across versions, same key as history.
- Without an attempt, an address resolves to the **latest successful attempt** — the same one later
  stages consume.
- The run's version stamp is derived from the run number (e.g. `2026.10.21.412`), so "version X" and
  "build 412" resolve to the same run.

**Storage layout** (Garage) mirrors the address:

```
runs/<pipelineId>/<run>/<step>/<attempt>/output.json
runs/<pipelineId>/<run>/<step>/<attempt>/files/…
runs/<pipelineId>/<run>/<step>/<attempt>/log
```

**File refs carry the full address** — `{ run, step, attempt, path, sha256 }` — so continues and
reruns reuse earlier outputs by reference without copying files, the harness can verify downloads,
and content dedup is possible later.

**Queries** the service supports: `run/step[@attempt]` → output; latest successful output of a step
across runs (promote; later the environments view); run by version stamp.

**API / CLI:** `GET /api/pipelines/{id}/runs/{run}/steps/{step}/output?attempt=…` and
`…/steps/{step}/files/{path}`; `pl step output <pipeline>#412/deploy-beta`,
`pl step file <pipeline>#412/build image.tar`.

## 6. Versioning

### 6.1 Versions

- **Pipeline version**: monotonic integer per pipeline, added by reconcile whenever the manifest
  changes; linked to the commit. Nobody writes a version by hand.
- **Entity versions**: steps and models each have `<id>.<version>`, incremented only when *that*
  entity's content changes, by content comparison:
  - model → its JSON Schema
  - step → image, input/output model versions, declared secrets, mappings, settings
  - **code hash** = one hash of the whole build on the manifest root: a code change makes a new
    pipeline version and bumps no entity. Not Bun's metafile input list: its import graph is incomplete.
  - stage placement / `when` wrapping are *pipeline* changes, not step changes
- A model change ripples to every step consuming it.
- Hash the **manifest**, not the source; describe must be deterministic (no env/time/network) or
  versions flap. Dependency changes reach the code hash. The manifest carries `harness: { bun, sdkHash }`; a Bun or SDK bump likewise
  makes a new pipeline version.
- **What makes a new build.** The config cursor (`ConfigCursor`) watches the binding path plus the
  local `file:`/`link:` dependencies of its `package.json` (e.g. `../src/sdk`), so a commit touching
  only an in-repo SDK still rebuilds the binary. Direct dependencies only; absolute specs and paths
  outside the repo are ignored.
- Versions are **immutable**; their manifests are kept, and their binaries while retention keeps
  them (§6.4).

### 6.2 Single active version and `switch`

New runs start on **one active version** of a pipeline:

```
Active(7)              # 8 added
  └─ switch → 8
Active(8)              # new runs start on 8; runs pinned to 7 carry on there
```

- Reconcile calls `switch` automatically. Rollback is `switch → 7`.
- A normal switch completes at once. Runs on older versions keep the version they pinned; since a
  run never starts a step a newer run already started (§5.3), an old-version run can't deploy after
  a newer one.
- `switch --cancel-running` drains instead, `Draining(7 → 8)`: other versions' in-flight attempts
  and runs are cancelled, and run requests meanwhile queue (latest-wins) and **bind to a version at
  start**, so they are mapped and validated against 8's triggers and input model.
- Reruns and continues need the run's version to be active; a run on an older version is re-triggered
  on the new one.
- A reconcile whose manifest fails checks never switches; the problems surface in binding status.
- No side-by-side canary of pipeline definitions: testing a new definition means switching to it.

### 6.3 History

History follows **identity** (step name), with definition changes as inline markers:

```
deploy-beta
  ● run 412  ok    3m10s   deploy-beta.3   ← changed in v8: output BetaDeployment.1 → .2
  ● run 409  ok    2m58s   deploy-beta.2
  ● run 401  fail  0m41s   deploy-beta.2
```

- Unchanged steps: one continuous history across pipeline versions.
- Changed steps: continuous history; stats can be scoped "since last change".
- Old runs render and validate against their own pinned model versions.
- Removed steps: history kept, shown as retired; re-adding the same name resumes it. Renames are a new
  identity (a `renamedFrom` hint can come later if missed).

### 6.4 Retention

`RetentionService` (`Retention:*`, a dry run by default) sweeps 2 minutes after start-up and then
daily; `RetentionPlanner` is one pure function over a snapshot of the bucket, the cluster and the
stores. Settings and defaults are in [runs-and-operations.md](../setup/runs-and-operations.md#retention).

- A run goes only when it is **both** beyond its pipeline's latest 20 and older than 183 days, and
  never while Running. Its objects go after the run is deleted and persistence has flushed;
  `run.json`, manifests and indexes belong to persistence and are never deleted by retention.
- A binary stays while an active or draining version, a retained run or a running CI run uses it,
  else it goes after a 7-day grace. Version entities and their manifests are kept: they are small and
  are the history.
- Leftovers: objects of deleted runs, `result.tar.gz` of finished attempts, stale trigger
  events, URL Secrets whose Job is gone or finished, and leaked or TTL-less finished Jobs, the last
  only with an Olve.Pipelines role and a pipeline of this instance.
- The run whose `publish-cli` files `/download` serves is pinned (`CliReleaseQuery`).
- **Per pipeline**, `pipeline({ retention: { keepLatestRuns, minRunAgeDays, buildGraceDays } })`
  overrides the three thresholds; `RetentionPolicies` reads the manifest of the version new runs
  start on. The policy lives in the definition rather than in runtime config so that it travels
  with the repo and is versioned like the rest of the pipeline.

## 7. GUI and CLI

- **Pipeline view**: stages, field-level wiring from mappings / model matches, `when` blocks,
  versions with diff.
- **Trigger form** generated from the input model; **run plan** preview (greyed-out stages whose
  `when` won't match).
- **Run view**: input, then each step's attempts with typed input/output rendered against the pinned
  model versions; skip reasons; superseded/rerun links.
- **Health** is over runs: running if any run is Running, else the latest Succeeded or Failed run
  (cancelled and superseded runs say nothing about health; skipped steps never count as unhealthy).
- **Live updates**: views open `/api/events` (change notices, never state) and re-fetch on a
  relevant notice ([runs-and-operations.md](../setup/runs-and-operations.md#live-events)).
- `pl`: `trigger --input k=v`, `run continue`, `step rerun [--continue]`, `pipeline switch <version>`,
  `pipeline manifest`, and local development (§7.1); the full set is in [cli.md](../setup/cli.md).

### 7.1 Local development

Three loops, all from the repo via `pl` (which shells out to Bun):

1. **Editor / describe** — `tsc` and the editor catch wiring errors; `pl dev describe` prints the
   manifest + describe-time checks from source; `pl dev validate` posts it to the service's
   validation endpoint for the C# reconcile checks without pushing.
2. **One step from source** — `pl dev run <step>` runs the handler with Bun directly (no compile),
   through the same harness path (validate input → handler → validate output → `<pipeline dir>/.olve/work/`).
   Input via `--input-file`, `--field k=v` (pipeline-input fields), or **`--from <pipeline>#<run>`**,
   which pulls a real run's input + outputs (file refs on demand) to reproduce or iterate on a step
   with production data. Secrets come from the local env or `--secret NAME=…` — never auto-pulled.
3. **One step in its real image** — `pl dev run <step> --docker` compiles the real binary and runs it
   in the step's declared image exactly like the pod (`/olve` mount, bundled loader, env layout),
   catching missing shells/tools/`/tmp` surprises.

`pl dev pipeline --field deployTo=beta [--until|--only|--from]` chains stages locally through a
local pool, evaluating `when`; it asks for confirmation before steps that declare secrets (they do
real things). The SDK exports `testHandler(step, input, { ctx })` for `bun test` with a fake `ctx`.
No "run on the cluster from a laptop" — pushing to a beta-bound branch covers that.

### 7.2 Step reliability

`StepReliabilityService` (`pl pipeline reliability`, the reliability page) reports how reliable a
pipeline's steps were; what it shows is defined in
[runs-and-operations.md](../setup/runs-and-operations.md#step-reliability). Its design:

- **A query plus one recorded fact.** Everything is derived from attempts on request, except why an
  attempt failed: a Failed attempt stores an `AttemptFailureKind` (the harness's exit-code kinds plus
  lost Job, failed launch, not run, declined). The own / infrastructure / timeout split is a mapping
  in the query (`AttemptFailures.Category`), so it can be retuned without touching stored state.
  Attempts recorded without a kind get one from the fixed start of their reason
  (`AttemptFailures.KindOf`); no match counts as the step's own.
- **Same input is not `InputHash`.** That hash is taken before external answers are added, and a file
  ref names the run and attempt that made the file, so it differs between runs for the same file. The
  query compares the stored input with file refs reduced to path and content hash, and only for steps
  with an own failure in the window.
- **Same code** is the manifest's `codeHash`, the step's entity version and the run's config
  snapshot. `codeHash` covers the whole program, so changing one handler makes every step's
  executions incomparable with earlier ones: that under-reports flakiness and never invents it.
- **The combined rate is observed, not multiplied.** `1 − Π(1 − rᵢ)` over step rates assumes every
  step runs in every run and ignores `retries`; counting the runs a no-reason failure stopped needs
  neither. A failure stops the run when it is past the step's first `retries` failures in that run.
- **Thresholds** for "above usual" are constants in the service, picked to stay quiet on a steady
  test step; they are not configurable.
- **It never fails a build**: nothing in scheduling reads the report and no step can gate on it.

### 7.3 API errors

Every non-2xx answer but 401/403 (empty) has one body, `ApiProblem`:
`{type, title, status, detail, errors: [{code, message, field?}], traceId}`, served as
`application/json`, with `type` `urn:olve-pipelines:<code>` and the codes
`not-found | conflict | invalid | unavailable | internal`.

- Domain code returns typed problems (`Shared/Problems`): `NotFoundProblem` (404), `ConflictProblem`
  (409: inactive version, blocked step, active attempts, superseded, already exists),
  `ValidationProblem` (400) and `UnavailableProblem` (503: Kubernetes, Garage, GitHub). Validation and
  conflict problems may carry a `field` (`input.sha`). A plain `ResultProblem` is 400.
- **Status precedence** 404 > 409 > 503 > 400: a missing resource or a conflicting state can't be
  fixed by retrying, and context prepended to a problem never downgrades its status.
- `ApiProblems` builds the body from a handler's problems; the contract's own request validation
  answers through `ApiProblemWriter` ([spec-first.md](spec-first.md)). Severity, source, tags and
  exception summaries are never returned. `traceId` is the W3C trace id and greps to the request's
  log lines.
- `UseApiErrors` turns unhandled exceptions into the same body: 503 with `Retry-After` for transient
  failures (network errors, timeouts, 5xx/429 from a dependency), 500 otherwise with a generic text,
  logged once with the stack; binding failures answer 400.
- Rejected: array bodies (no place for status-level fields) and string codes on `ResultProblem`
  (easy to drop on `Prepend`).

## 8. Security

- The describe Job and step Jobs run under the runner hardening (gVisor, PSS baseline,
  NetworkPolicy). Describe needs the repo + npm registry, the init container Garage.
- Step pods can reach services in the cluster, so **every service endpoint is authenticated**, and
  artifacts go in/out only via presigned URLs.
- Manifests and step outputs are **untrusted input**: schema-validated with size caps before use.
- Repo code (describe, handlers, checks, trigger mappers) only ever executes in sandboxed Jobs, never
  in the service.
- Secret values are masked in what a step prints and in its failure message: given the declared
  names in `OLVE_SECRETS`, the binary runs the step as its own child and filters its output
  (`src/sdk/README.md`). Outputs and files are data and are not masked.

## 9. Open questions

- **Output caching** (reuse a succeeded attempt's output for the same step, code and input) and
  **environments / tags** (what is deployed where, promote, rollback warnings) are not built; see the
  [roadmap](roadmap.md).
- No warning when a continue or rerun would roll an environment back to an older build.
- Describe determinism is not enforced: a describe that reads the environment, time or network makes
  versions flap.
- Other model sources than zod are possible, since JSON Schema is the interchange.
