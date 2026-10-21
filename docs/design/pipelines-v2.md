# Olve.Pipelines v2 — typed pipelines in TypeScript

Status: **design agreed, spikes done, not implemented** (2026-10-21). Built on the `v2` branch and deployed as a
separate instance (`pipelines-v2`) alongside v1 until every pipeline has migrated.

## 1. Goals

1. **Typed data between steps.** Every step declares the models it consumes and the model it
   produces. Wiring mistakes are compile errors; bad outputs fail at the step that produced them.
2. **Pipeline behavior defined in TypeScript** as sequential stages of parallel steps. A step may
   still just call a shell script — the typing describes the data, not how it is produced.
3. **Introspectable.** The models, steps and wiring are plain data in a manifest, so the GUI and
   `pl` can render forms, run plans, typed inputs/outputs and history without running user code.

Non-goals for v2.0: wait steps (v2.1), matrix steps, output caching, environments/tags, SDK publishing story,
arbitrary-payload webhook mapping. See [Deferred](#11-deferred).

## 2. Concepts at a glance

| Concept | What it is |
|---|---|
| **Model** | A named, nominal zod schema (`BuildArtifact`). Emitted as JSON Schema. |
| **Step** | A contract: name, image, inputs (models or plain schemas), output model, secrets. No behavior. |
| **Handler** | The implementation of one step: `(input, ctx) => output`, in `handlers/<step-name>.ts`. |
| **Library step** | An SDK-provided step + handler pair with typed config (`script`, `gitCheckout`, `imageBuild`). |
| **Pipeline** | Input model, triggers, and an ordered list of **stages**; each stage runs its steps in parallel. |
| **Run** | One execution of a pinned pipeline version: an input plus the outputs of each step (the **pool**). |
| **Attempt** | One execution of one step within a run. Reruns add attempts. |
| **Version** | Immutable snapshot. Pipelines, steps and models each have `<id>.<version>`. |

## 3. Authoring

### 3.1 Layout

```
.pipelines/              # binding path (overridable — several pipelines per repo = several bindings)
  models.ts              # level 1: data models
  pipeline.ts            # level 2: steps (contracts) + pipeline (input, triggers, stages)
  handlers/<step-name>.ts  # level 3: one implementation per custom step (default export)
  scripts/               # shell scripts, embedded into the binary at build
```

Dependency direction: `models ← steps ← handlers`, `models ← pipeline`. Handlers never depend on the
pipeline; their types come from the step definition alone.

- **Contract files** (`models.ts`, `pipeline.ts`) contain contracts only — no handler code. Describe
  evaluates them alone, and they are external to every handler bundle (§6.1).
- **Handlers by convention**: `handlers/<step-name>.ts` default-exports `implement(step, fn)`. The
  build generates a registry of literal imports (computed dynamic imports would load from disk at
  runtime). A custom step without a handler file is a describe error; library steps need none.
- **Scripts are embedded**: `import script from "../scripts/x.sh" with { type: "file" }`. The repo
  is not in the step container; the harness copies embedded scripts out before running them.

### 3.2 Models

```ts
import { model, z, fileRef } from "@olve/pipelines";

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
- **Files are references**, not inline data. Structured output stays small (cap TBD, ~64 KB); large
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
import { step, secret, Source } from "@olve/pipelines";
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
  image: "alpine:latest",
  input: { artifact: M.BuildArtifact },
  output: M.BetaDeployment,
  secrets: [SSH_PRIVATE_KEY],
  timeoutSeconds: 900,          // optional
});

export const deployProd = step({
  name: "deploy-prod",
  image: "alpine:latest",
  input: { artifact: M.BuildArtifact },
  output: M.ProdDeployment,
  secrets: [SSH_PRIVATE_KEY],
});
```

- A step's **name is its identity** (history, scheduling, promotion gates are keyed on it).
- Each **input entry** is a model, `optional(model)` (see skip propagation, §3.6), or a plain zod
  schema (`{ repo: z.string() }`). Plain-schema entries have no model to match on, so the step
  **requires a mapper**.
- Steps declare the **secrets** they use; only those are mounted into the step's Job, and
  `ctx.secrets` is typed to exactly that set.
- `image` can be any image — the harness brings its own runtime (see [§5.1](#51-harness)).

### 3.4 Library steps

First-class, SDK-provided step + handler pairs. Each has **its own typed config properties** and
SDK-provided models (`Source`, `BuiltImage`, …) so pipelines share vocabulary.

```ts
import testSh from "./scripts/test.sh" with { type: "file" };

export const test = steps.script({ name: "test", image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
               script: testSh, input: { source: Source }, output: TestReport /* optional */ });

export const checkout = steps.gitCheckout({ name: "checkout", depth: 1, submodules: false });
//   input { repo: z.string(), sha: z.string() } (plain schemas → mapper required) → output Source { repo, sha, tree: fileRef }

steps.imageBuild({ name: "image", dockerfile: "Dockerfile", context: ".", tag: "olve-pipelines",
                   buildArgs: { CONFIGURATION: "Release" } });
//   input { source: Source } → output BuiltImage { ref, digest, tar: fileRef }
```

- `script` takes an **embedded** script (the `with { type: "file" }` import), exposes inputs as env
  vars **and** `input.json`; if an output model is declared it reads and validates `output.json`.
  This is the zero-TypeScript migration path from v1.
- Config values that depend on the run (e.g. a tag containing the version) may be functions of the
  inputs; they execute inside the handler, so describe stays pure.
- Helpers used by library steps are also exported for custom handlers (`ctx.checkout(sha)`).

### 3.5 Handlers

```ts
// handlers/deploy-beta.ts
import { implement } from "@olve/pipelines";
import { deployBeta } from "../pipeline";
import deployScript from "../scripts/deploy-beta.sh" with { type: "file" };

const RELEASE = "olve-pipelines";
const URL = "https://pipelines-beta.ovea.pro";

export default implement(deployBeta, async ({ artifact }, ctx) => {
  await ctx.sh(deployScript, {
    env: { RELEASE, VERSION: artifact.version,
           IMAGE_TAR: await ctx.files.fetch(artifact.image),
           CHART_DIR: await ctx.files.fetch(artifact.chart) },
  });
  await ctx.retry({ attempts: 5, delaySeconds: 5 }, () => ctx.http.expectOk(`${URL}/api/health`));
  return { release: RELEASE, version: artifact.version, url: URL };
});
```

`ctx` surface (initial): `sh`, `sh.text`, `readText`, `readJson`, `files.fetch`, `output.file`,
`output.dir`, `http`, `retry`, `secrets`, `log`, `signal` (cancellation/timeout), `run` (run id,
number, derived version), `skip(reason)` (runtime escape hatch — prefer `when`).

`ctx.sh` runs a command string or an embedded script (copied out to `/olve/tmp` first) via the
image's shell, detected from `/bin/sh`, `/busybox/sh`, `/bin/bash`; shell-less images (distroless)
get a clear error, not an `ENOENT`. Children use the image's own libc and tools.

Return type is `Infer<Output> | Skipped`: a handler produces a valid output or explicitly skips.

### 3.6 Pipeline

```ts
// checkout, test (§3.4), build, deployBeta, deployProd (§3.3) are defined above in pipeline.ts;
// publishCli (a step consuming BuildArtifact) and aoeTriage (a failure handler) are defined elsewhere.
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
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
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

**Failure handlers.** v1's failure handlers carry over as `.onFailure(handler, { steps? })`; the
failure context becomes a typed `FailureContext` model instead of `$PIPELINE_*` env vars.

### 3.7 Pipeline-build checks

Enforced in three layers: **TypeScript** (the builder accumulates available outputs in its type),
**describe** (checks only the evaluated source can answer) and **reconcile** (the service re-runs
every manifest-level check and rejects invalid configs — it never trusts that the compiler ran, and
script handlers bypass it anyway). The spike verified every TS check below (incl. ambiguity, fully);
the compile-fail checker runs in SDK CI against both TS lines.

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
| Custom step without a handler file | — | ✓ (registry generation) | — |
| Output nothing consumes | — | | warning (fine for gate steps like `test`) |

### 3.8 Wait steps (v2.1)

> **Deferred to v2.1.** Waits don't fit the "attempt = one Job" model and get designed properly
> then. Nothing in v2.0 implements or reserves them; the notes below are the starting point.

A `wait` step has **no pod while waiting**. The attempt sits in `Waiting` until it is resolved with a
value that is validated against the step's **output model** — the output model *is* the human's or
external system's input.

```ts
steps.wait({
  name: "visual-review",
  input: { diff: VisualDiff },
  output: ReviewDecision,             // z.object({ approved: z.boolean(), note: z.string().optional() })
  resolveBy: {
    manual: true,                     // GUI / pl form generated from ReviewDecision
    callback: true,                   // per-attempt URL; caller POSTs a ReviewDecision
    check: { every: "5m", async run({ diff }, ctx) {
      const s = await ctx.http.getJson(`https://percy.example/api/builds/${diff.buildId}`);
      return s.state === "approved" ? { approved: true } : ctx.notYet();
    } },
  },
  timeoutHours: 72,                   // optional; timeout = failure
});
```

| Mode | Resolver must know Olve.Pipelines? | Cost |
|---|---|---|
| Manual | n/a | none |
| Callback | yes | none |
| Scheduled check | no | one short Job per interval running the `check` function |

Rule of thumb: if code must *actively* watch with low latency (SSE, tight polling), it is an ordinary
step with a long timeout; if the result is pushed or can be checked periodically, it is a `wait`.

Semantics: a waiting run is abandoned when superseded or when its version is switched out; it does
not block a `switch` drain.

**Execution model — waits are not Jobs.** An attempt has an **execution kind**, and the scheduler
dispatches by kind instead of assuming "attempt = one K8s Job":

| Kind | Lifecycle | K8s footprint |
|---|---|---|
| `job` (every ordinary/library step) | Scheduled → InProgress → Succeeded/Failed | exactly one Job per attempt |
| `wait` | Scheduled → Waiting → Succeeded (resolved) / Failed (timeout) / Cancelled | none while waiting; zero or more short **probe** Jobs |

- A waiting attempt is a **persisted state with a schedule** (`nextCheckAt`, `deadline`), driven by the
  service: a timer fires checks, the API/callback endpoint delivers resolutions, a timeout fails it.
  It survives restarts like any other state.
- **Probes** (scheduled-check Jobs) are children of the attempt, not attempts themselves: many per
  attempt, each returning `notYet` or an output. They don't consume the step's slot or count as
  attempts in history (shown as a probe log under the attempt).
- A `Waiting` attempt **holds its step's slot** (one in progress per step): latest-wins and supersede
  apply to it like any in-progress step — a newer run's arrival at the wait abandons the older wait.
- Resolution is the only way out besides timeout/cancel: whichever resolver (manual, callback, probe)
  delivers a valid output first wins; later deliveries are rejected (attempt no longer Waiting).
- In the backend this is one executor per kind behind a common interface (`JobStepExecutor`,
  `WaitStepExecutor`), so further kinds can be added without touching the stage scheduler.


## 4. Manifest

`describe` mode evaluates `models.ts` + `pipeline.ts` (never handlers) and emits:

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
    { "steps": [ { "name": "checkout", "kind": "gitCheckout", "image": "…",
                   "input": { "repo": { "ref": "input.repo" }, "sha": { "ref": "input.sha" } },
                   "output": "Source" } ] },
    { "steps": [ "…build, test…" ] },
    { "steps": [ { "name": "deploy-beta", "image": "alpine:latest",
                   "input": { "artifact": { "ref": "build" } }, "output": "BetaDeployment",
                   "secrets": ["SSH_PRIVATE_KEY"], "handlerHash": "sha256:…" } ] },
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
--compile`, ~91 MB, ~38 MB gzipped) stored in Garage as `step.tar.gz`. It has three modes:

- `describe` → the manifest.
- `run <step>` → read `/olve/input.json`, validate against the step's input, call the handler,
  validate the return value strictly against the output model, write `/olve/work/output.json` (files
  under `/olve/work/files/`), pack both into `result.tar.gz` and PUT it to `OLVE_RESULT_URL`.
- `map-trigger <trigger>` → short-lived helper mapping one event to pipeline input.

**`run` contract** (authoritative detail: `sdk/README.md`; manifest format: `sdk/manifest.schema.json`).
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

**Build/describe Job** (on `oven/bun:<pinned>-slim` — never alpine, where `bun-linux-x64` silently
yields musl): fetch the repo tarball → `bun install --frozen-lockfile` → generate the handler
registry → `bun build --compile --target=bun-linux-x64 --no-compile-autoload-dotenv
--no-compile-autoload-bunfig --bytecode --format=esm` → verify the ELF `PT_INTERP` → PUT
`step.tar.gz` (presigned) → print the manifest between `---OLVE-MANIFEST-BEGIN---` /
`---OLVE-MANIFEST-END---` on stdout; the service reads it from the pod log (termination messages
cap at 4 KB). No callbacks to the API. ~1.3 s plus the first image pull.

The service never executes TypeScript itself.

### 5.2 Runs, pool and attempts

- A run **pins one pipeline version** and stores its input plus every step's output (the **pool**).
  Each job receives only the models its step declared (or its mapped paths), keeping contracts honest.
- The run's **version stamp is derived from the run** (e.g. `<date>.<run number>`; format TBD), so it
  is known before `build` starts.
- A step's outcome is `Succeeded`, `Failed`, `Skipped` (with reason), `Cancelled`, `Obsolete`. A failed step halts the run at its stage barrier.
- **Explicit reruns are attempts.** Rerunning a step adds an attempt in the same run with the same
  pinned version and pool input; the operator chooses *rerun only* or *rerun and continue*. Later
  stages consume the **latest successful attempt**; earlier attempts stay in history. Rerunning a
  whole run creates a new run with the same input, linked to the original.
- **Continue** a run with a changed input (e.g. `deployTo: beta → prod`) keeps completed stages and
  re-evaluates `when` for the rest. Only fields no completed step consumed may change (e.g. `sha`
  cannot) — this is "promote what you tested in beta".
- **No automatic retries** unless a step opts in with `retries: n` (an idempotency claim).

### 5.3 Scheduling

- **Keyed on step id, not version.** At most one `InProgress` job per (pipeline, step); latest-wins
  among scheduled jobs; v1 semantics carry over.
- **An automatic newer run supersedes an older run as a whole**: the older run is marked superseded
  (linked to the newer one) and its remaining stages are abandoned: scheduled attempts are cancelled,
  in-progress attempts finish (a half-done deploy is never killed) but the run never advances.
  Explicit reruns follow the per-step rules.
- **Trigger de-duplication applies to automatic triggers only** (webhook, poll, reconcile): keyed on
  (pipeline, mapped input) by default, so the same SHA from webhook and poll yields one run. Since
  mappers are code, de-dup needs the mapped input: one `map-trigger` Job per event, de-dup after it.
  Explicit actions (GUI, `pl`, API) always create work.
- **Promotion gates** stay keyed on step id: a blocked step holds its run at that step; unblock
  resumes the due promotion. A braked step stays braked across version switches.

### 5.4 Persistence

Runs, pools, attempts, pipeline versions, enable state and an in-progress `switch` are persisted and
must survive a restart — in particular during v2's own self-deploy.

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
`…/steps/{step}/files/{path}`; `pl run output <pipeline> 412 deploy-beta`,
`pl run file <pipeline> 412 build image.tar`.

## 6. Versioning

### 6.1 Versions

- **Pipeline version**: monotonic integer per pipeline, added by reconcile whenever the manifest
  changes; linked to the commit. Replaces v1's hand-written `version:` field.
- **Entity versions**: steps and models each have `<id>.<version>`, incremented only when *that*
  entity's content changes, by content comparison:
  - model → its JSON Schema
  - step → image, input/output model versions, declared secrets, mappings, handler hash
  - **handler hash** = hash of that handler's own bundle output, built separately with the contract
    files external (tree-shaken, so imported shared code and embedded scripts count at function
    granularity). Not Bun's metafile input list — its import graph is incomplete.
  - stage placement / `when` wrapping are *pipeline* changes, not step changes
- A model change ripples to every step consuming it.
- Hash the **manifest**, not the source; describe must be deterministic (no env/time/network) or
  versions flap. Lockfile changes legitimately bump every handler — the GUI labels it "dependency
  change". The manifest carries `harness: { bun, sdkHash }`; a Bun or SDK bump likewise
  legitimately bumps all handlers.
- Versions are **immutable** and retained while any retained run references them.

### 6.2 Single active version and `switch`

Only **one version of a pipeline runs at a time**. Replacing a version is one composite operation:

```
Active(7)              # 8 added, idle
  └─ switch → 8
Draining(7 → 8)        # 7 starts no new runs; in-flight jobs finish
  └─ no Scheduled/InProgress jobs on 7
Active(8)              # queued trigger (if any) starts on 8
```

- Reconcile calls `switch` automatically. Rollback is `switch → 7`.
- Triggers during draining are queued (latest-wins collapses them) and **bind to a version at start**,
  so they are mapped and validated against v8's triggers and input model.
- `switch --cancel-running` cancels v7's in-flight jobs instead of waiting.
- A reconcile whose manifest fails checks never switches; the problems surface in binding status.
- Paused runs (waiting for continue/promote, braked) don't block draining and become
  un-continuable afterwards (the GUI says so; re-trigger on the new version).
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

## 7. GUI and CLI

- **Pipeline view**: stages, field-level wiring from mappings / model matches, `when` blocks,
  versions with diff.
- **Trigger form** generated from the input model; **run plan** preview (greyed-out stages whose
  `when` won't match).
- **Run view**: input, then each step's attempts with typed input/output rendered against the pinned
  model versions; skip reasons; superseded/rerun links.
- **Health summary** redefined over runs/stages — `Skipped` never counts as unhealthy.
- `pl`: `trigger --input k=v`, `run continue`, `step rerun [--continue]`, `pipeline switch <version>`,
  `pipeline manifest` (replaces `pipeline document`), and local development (§7.1).

### 7.1 Local development (wanted early)

Three loops, all from the repo via `pl` (which shells out to Bun):

1. **Editor / describe** — `tsc` and the editor catch wiring errors; `pl dev describe` prints the
   manifest + describe-time checks from source; `pl dev validate` posts it to the service's
   validation endpoint for the C# reconcile checks without pushing.
2. **One step from source** — `pl dev run <step>` runs the handler with Bun directly (no compile),
   through the same harness path (validate input → handler → validate output → `<pipeline dir>/.olve/work/`).
   Input via `--input-file`, `--input k=v` (pipeline-input fields), or **`--from <pipeline>#<run>`**,
   which pulls a real run's input + outputs (file refs on demand) to reproduce or iterate on a step
   with production data. Secrets come from the local env or `--secret NAME=…` — never auto-pulled.
3. **One step in its real image** — `pl dev run <step> --docker` compiles the real binary and runs it
   in the step's declared image exactly like the pod (`/olve` mount, bundled loader, env layout),
   catching missing shells/tools/`/tmp` surprises.

`pl dev pipeline --input deployTo=beta [--until|--only|--from]` chains stages locally through a
local pool, evaluating `when`; it asks for confirmation before steps that declare secrets (they do
real things). The SDK exports `testHandler(step, input, { ctx })` for `bun test` with a fake `ctx`.
No "run on the cluster from a laptop" — pushing to a beta-bound branch covers that.

## 8. Security

- The describe Job and step Jobs run under the existing runner hardening (gVisor, PSS baseline,
  NetworkPolicy). Describe needs the repo + npm registry, the init container Garage — both already
  allowed; **no NetworkPolicy change needed**.
- Runner pods can reach everything in apps/apps-beta (both Pipelines APIs, Garage), so **every
  service endpoint is authenticated**, and artifacts go in/out only via presigned URLs.
- Manifests and step outputs are **untrusted input**: schema-validated with size caps before use.
- Repo code (describe, handlers, checks, trigger mappers) only ever executes in sandboxed Jobs, never
  in the service.
- Secret values are masked in outputs and logs (approach TBD during implementation).

## 9. Mapping from v1

| v1 | v2 |
|---|---|
| `config.yaml` production + processing steps | stages; first stage ≈ production, later stages generalize processing |
| `ProductionJob` / `ProcessingJob`, job groups | runs → attempts (one job per attempt) |
| ArtifactBundle (combined zip) | per-step bundle dir + `output.json`; `fileRef()` for files |
| "find the bundle dir containing version.txt", `commit.txt` | typed fields on `BuildArtifact` |
| Promotion gate + promote | gate keyed on step id; promote = continue / rerun-and-continue |
| Processing trigger target | continue a run from a step |
| Poll trigger (`$SECRET:` headers) | `.onPoll({ …, secrets }, mapper)`, headers type-checked against `secrets` |
| Webhook / poll overlap fix | automatic-trigger de-duplication |
| Failure handlers | `.onFailure(handler, { steps })`, typed `FailureContext` |
| All pipeline secrets mounted in every job | per-step declared secrets |
| `version:` field | service-assigned pipeline/entity versions |
| Retention (6 months / latest 20, Job TTL 7d) | extended to versions, pools and file refs |

## 10. Edge cases (decide during implementation)

Output size cap; file-ref lifetime vs retention (check refs exist before scheduling); per-step
timeouts and cancellation reaching child processes; secret masking; restart persistence; warning when
a promote/rerun would roll an environment back to an older build; describe determinism enforcement.

## 11. Deferred

- **Matrix steps** (`step.matrix([...])`) — near future.
- **Output caching** by (step version, input hash) — near future; store the input hash per attempt now.
- **Environments / tags** (environment strip, promote-beta-to-prod, rollback warnings).
- **Wait steps** (§3.8) — v2.1, including arbitrary-payload callback mapping.
- **SDK publishing** (`@olve/pipelines` distribution and version compatibility with the service).
- **TypeSpec** (or other) model sources — possible later since JSON Schema is the interchange.

## 12. Spikes — done (2026-10-21)

All four passed; findings are folded into this spec. Details, numbers and gotchas:
[pipelines-v2-spikes.md](pipelines-v2-spikes.md) (code under `spikes/`).

1. **Harness in any image** — PASS (glibc binary + bundled loader; §5.1).
2. **One binary, many modes** — PASS (deterministic manifest; per-handler bundle hashes, not the
   metafile; §6.1).
3. **Describe sandbox** — PASS (~1.3 s; manifest via stdout sentinels; §5.1, §8).
4. **Type-level builder** — PASS (every TS check in §3.7; tuple mappers, describe source analysis).

Still to validate early: one full busybox-wget `harness-base` pod end to end.

## 13. Transition plan

**v2 is fully breaking.** Anything in v1 that conflicts with this design (the production/processing
split, job groups, the combined ArtifactBundle zip, the `config.yaml` format, all-secrets-mounted,
the hand-written `version:` field, …) is removed on the `v2` branch rather than kept for
compatibility. Nothing is migrated from a v1 instance's state.

1. **v2 branch, v1 feature-frozen** (critical fixes only, ported to `v2`). v2 is deployed as a
   separate instance (`pipelines-v2`), initially beta-only, by a v1 pipeline bound to the `v2` branch.
   It needs its own Helm release + hostname, Garage bucket, Authentik app, Cloudflare tunnel route for
   webhooks, and runner namespace (or distinct job labels in `olve-runners`).
2. **Migrate pipelines one by one**, least critical first: add `.pipelines/pipeline.ts` next to
   `config.yaml` (different files, both instances read their own), copy the pipeline secret, bind on
   v2, test with `deployTo: beta`, then move the GitHub webhook to v2 and pause/unbind on v1.
   **Never both active** — two instances reacting to one push means two concurrent deploys. Rollback =
   webhook back to v1. Delete `config.yaml` once stable.
3. **Olve.Pipelines last**: once v2 deploys itself, retire v1, promote `v2` to `main`, optionally move
   v2 to the `pipelines` hostname.
4. v1 run history is not migrated; it stays readable until v1 shuts down and v1 bundles/logs follow
   existing retention. `pl` gets a v2 profile (or a v2 build from the branch).
