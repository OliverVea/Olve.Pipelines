# Olve.Pipelines v2 — typed pipelines in TypeScript

Status: **design agreed, not implemented** (2026-10-21). Built on the `v2` branch and deployed as a
separate instance (`pipelines-v2`) alongside v1 until every pipeline has migrated.

## 1. Goals

1. **Typed data between steps.** Every step declares the models it consumes and the model it
   produces. Wiring mistakes are compile errors; bad outputs fail at the step that produced them.
2. **Pipeline behavior defined in TypeScript** as sequential stages of parallel steps. A step may
   still just call a shell script — the typing describes the data, not how it is produced.
3. **Introspectable.** The models, steps and wiring are plain data in a manifest, so the GUI and
   `pl` can render forms, run plans, typed inputs/outputs and history without running user code.

Non-goals for v2.0: matrix steps, output caching, environments/tags, SDK publishing story,
arbitrary-payload webhook mapping. See [Deferred](#11-deferred).

## 2. Concepts at a glance

| Concept | What it is |
|---|---|
| **Model** | A named, nominal zod schema (`BuildArtifact`). Emitted as JSON Schema. |
| **Step** | A contract: name, image, input models, output model, secrets. No behavior. |
| **Handler** | The implementation of one step: `(input, ctx) => output`. |
| **Library step** | An SDK-provided step + handler pair with typed config (`script`, `gitCheckout`, `imageBuild`, `wait`). |
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
  handlers/*.ts          # level 3: implementations
  scripts/               # shell scripts handlers may call
```

Dependency direction: `models ← steps ← handlers`, `models ← pipeline`. Handlers never depend on the
pipeline; their types come from the step definition alone.

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
  data lives in the bundle and is referenced via `fileRef()`.
- zod over TypeSpec: the service and GUI only consume JSON Schema (zod v4 `z.toJSONSchema`). Because
  JSON Schema is the interchange format, another model source can be added later without touching
  the service.

### 3.3 Steps

```ts
import { step, secret } from "@olve/pipelines";
import * as M from "./models";

export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for image import + helm on the host.");

export const deployBeta = step({
  name: "deploy-beta",
  image: "alpine:latest",
  input: { artifact: M.BuildArtifact },
  output: M.BetaDeployment,
  secrets: [SSH_PRIVATE_KEY],
  timeoutSeconds: 900,          // optional
});
```

- A step's **name is its identity** (history, scheduling, promotion gates are keyed on it).
- Steps declare the **secrets** they use; only those are mounted into the step's Job, and
  `ctx.secrets` is typed to exactly that set.
- `image` can be any image — the harness brings its own runtime (see [§5.1](#51-harness)).

### 3.4 Library steps

First-class, SDK-provided step + handler pairs. Each has **its own typed config properties** and
SDK-provided models (`Source`, `BuiltImage`, …) so pipelines share vocabulary.

```ts
steps.script({ name: "test", image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
               script: "scripts/test.sh", input: { source: Source }, output: TestReport /* optional */ });

steps.gitCheckout({ name: "checkout", depth: 1, submodules: false });
//   input { repo, sha } → output Source { repo, sha, tree: fileRef }

steps.imageBuild({ name: "build", dockerfile: "Dockerfile", context: ".", tag: "olve-pipelines",
                   buildArgs: { CONFIGURATION: "Release" } });
//   input { source: Source } → output BuiltImage { ref, digest, tar: fileRef }

steps.wait({ … });   // see §3.8
```

- `script` exposes inputs as env vars **and** `input.json`; if an output model is declared it reads
  and validates `output.json`. This is the zero-TypeScript migration path from v1.
- Config values that depend on the run (e.g. a tag containing the version) may be functions of the
  inputs; they execute inside the handler, so describe stays pure.
- Helpers used by library steps are also exported for custom handlers (`ctx.checkout(sha)`).

### 3.5 Handlers

```ts
import { implement } from "@olve/pipelines";
import { deployBeta } from "../pipeline";

const RELEASE = "olve-pipelines";
const URL = "https://pipelines-beta.ovea.pro";

export default implement(deployBeta, async ({ artifact }, ctx) => {
  await ctx.sh("scripts/deploy-beta.sh", {
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

Return type is `Infer<Output> | Skipped`: a handler produces a valid output or explicitly skips.

### 3.6 Pipeline

```ts
export default pipeline({
  name: "Olve.Pipelines",
  description: "Build, deploy beta then prod, publish the pl CLI.",
  input: M.DeployRequest,
})
  .onPush(e => ({ repo: e.repository, sha: e.after,
                  deployTo: e.ref === "refs/heads/main" ? "prod" : "beta" }))
  .onPoll({ url, headers: { Authorization: "$SECRET:GITHUB_TOKEN" }, valuePath: "sha", intervalSeconds: 300 },
          value => ({ sha: value }))
  .stage(checkout, in => ({ repo: in.input.repo, sha: in.input.sha }))
  .stage([build, in => ({ source: in.checkout })], [test, in => ({ source: in.checkout })])
  .stage(deployBeta)                                   // no mapper: match inputs by model
  .when({ deployTo: "prod" }, p => p
    .stage(deploy)
    .when({ publishCli: true }, p => p
      .stage(publishCli)))
  .onFailure(aoeTriage, { steps: [deploy] });
```

**Stages.** Sequential; the steps within a stage run in parallel. A stage is a **barrier**: the next
stage starts only when every step of the current one has succeeded (or been skipped). This is what
gates deploys on tests — no declaration needed.

**Triggers.** Each trigger maps its event to the pipeline input model; the mapping is type-checked
against it. Manual triggers (GUI / `pl trigger --input deployTo=beta`) use a form generated from the
input model. Input is validated at trigger time; invalid input is a 400, never a half-run.

**Input mapping.** By default a step's inputs are matched **by model** from what is available (the
pipeline input + outputs of earlier stages). An optional per-step mapper builds the input
explicitly:

- `.stage(step, in => ({ … }))` for a single step; `.stage([a, mapA], [b, mapB])` for a parallel stage.
- `in` is keyed by **step name**, plus `input` for the pipeline input.
- `in` holds typed **references** (`Ref<T>`), not values: only property access and literals compile.
  Describe calls the mapper with recording proxies and stores a **path mapping** in the manifest
  (`{ source: "checkout" }`, `{ sha: "input.sha" }`); the service builds `input.json` from it without
  running TypeScript, and the GUI draws exact field-level wiring.
- Use a mapper to adapt library steps to your models, to resolve ambiguity (two producers of the
  same model), or to fix constants.

**Conditions.** `when` blocks are declarative field matches on **literal-like pipeline-input fields**
(enums, booleans, literals; value or array of values). Typed against the input model — `{ deployTo:
"prd" }` and `{ sha: "abc" }` are compile errors. Conditions are data in the manifest, so the GUI can
show a **run plan** before a run starts.

**Skip propagation.** A step whose `when` does not match is skipped and produces no output. Any later
step needing that output is skipped too, with a chained reason. Outputs produced inside a `when` are
only *maybe* available afterwards: a later step must declare them `optional(Model)` (handler sees
`T | undefined`), or it is a compile error.

**Failure handlers.** v1's failure handlers carry over as `.onFailure(handler, { steps? })`; the
failure context becomes a typed `FailureContext` model instead of `$PIPELINE_*` env vars.

### 3.7 Pipeline-build checks

Enforced in two layers: **TypeScript** (the builder accumulates available outputs in its type) and
**reconcile** (the service re-runs every check on the manifest and rejects invalid configs — it
never trusts that the compiler ran, and script handlers bypass it anyway).

| Check | TS | Reconcile |
|---|---|---|
| Input model / mapped ref not available at this stage (incl. same-stage siblings) | ✓ | ✓ |
| Two earlier producers of a model with no mapper (ambiguity) | best-effort | ✓ |
| Maybe-available (inside `when`) output used as required input | ✓ | ✓ |
| Mapper type mismatch / missing field / logic on refs | ✓ | ✓ |
| Template-string or computed value in a mapper | lint | ✓ (proxy throws on coercion) |
| `when` on unknown / non-literal field or invalid value | ✓ | ✓ |
| Trigger mapping doesn't produce valid pipeline input | ✓ | ✓ |
| Step uses an undeclared secret | ✓ | ✓ |
| Duplicate step name | ✓ | ✓ |
| Step without handler | — | ✓ (bundling) |
| Output nothing consumes | — | warning (fine for gate steps like `test`) |

### 3.8 Wait steps

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

**Scope for v2.0: the model must support waits** (`Waiting` status, `wait` kind in the manifest); the
implementation can follow later.

## 4. Manifest

`describe` mode evaluates `models.ts` + `pipeline.ts` (never handlers) and emits:

```json
{
  "apiVersion": "2.0",
  "name": "Olve.Pipelines",
  "input": "DeployRequest",
  "models": { "DeployRequest": { "type": "object", "properties": { "…": {} } } },
  "secrets": [ { "name": "SSH_PRIVATE_KEY", "description": "…" } ],
  "triggers": [ { "kind": "push" }, { "kind": "poll", "url": "…", "valuePath": "sha", "intervalSeconds": 300 } ],
  "stages": [
    { "steps": [ { "name": "checkout", "kind": "gitCheckout", "image": "…",
                   "input": { "repo": "input.repo", "sha": "input.sha" }, "output": "Source" } ] },
    { "steps": [ { "name": "deploy-beta", "image": "alpine:latest",
                   "input": { "artifact": "build" }, "output": "BetaDeployment",
                   "secrets": ["SSH_PRIVATE_KEY"], "handlerHash": "sha256:…" } ] },
    { "when": { "deployTo": ["prod"] }, "stages": [ "…" ] }
  ],
  "failureHandlers": [ "…" ]
}
```

Trigger mapping functions (`onPush`, `onPoll`) are code; they run in a short Job from the compiled
binary when an event arrives (or describe records them as path mappings when they are pure — TBD in
the spike).

## 5. Runtime

### 5.1 Harness

The pipeline compiles (at reconcile, per commit) into **one standalone executable** (`bun build
--compile`) stored in Garage. It has three modes:

- `describe` → the manifest.
- `run <step>` → read `/olve/input.json`, validate against the step's input models, call the handler,
  validate the return value against the output model, write `/olve/output.json`, upload referenced
  files.
- `check <step>` / `map-trigger <trigger>` → short-lived helpers for wait checks and trigger mapping.

An **init container** copies the executable — plus its own C library and loader — into a shared
volume; the step container runs `/olve/step run <name>`. Bringing our own libc makes "any image"
true: glibc images, Alpine (musl) and libc-less images like kaniko all work. (Spike — §12.)

The service never executes TypeScript itself.

### 5.2 Runs, pool and attempts

- A run **pins one pipeline version** and stores its input plus every step's output (the **pool**).
  Each job receives only the models its step declared (or its mapped paths), keeping contracts honest.
- The run's **version stamp is derived from the run** (e.g. `<date>.<run number>`; format TBD), so it
  is known before `build` starts.
- A step's outcome is `Succeeded`, `Failed`, `Skipped` (with reason), `Waiting`, `Cancelled`,
  `Obsolete`. A failed step halts the run at its stage barrier.
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
  (linked to the newer one) and its remaining stages are abandoned. Explicit reruns follow the
  per-step rules.
- **Trigger de-duplication applies to automatic triggers only** (webhook, poll, reconcile): keyed on
  (pipeline, mapped input) by default, so the same SHA from webhook and poll yields one run. Explicit
  actions (GUI, `pl`, API) always create work.
- **Promotion gates** stay keyed on step id: a blocked step holds its run at that step; unblock
  resumes the due promotion. A braked step stays braked across version switches.

### 5.4 Persistence

Runs, pools, attempts, pipeline versions, enable state and an in-progress `switch` are persisted and
must survive a restart — in particular during v2's own self-deploy.

## 6. Versioning

### 6.1 Versions

- **Pipeline version**: monotonic integer per pipeline, added by reconcile whenever the manifest
  changes; linked to the commit. Replaces v1's hand-written `version:` field.
- **Entity versions**: steps and models each have `<id>.<version>`, incremented only when *that*
  entity's content changes, by content comparison:
  - model → its JSON Schema
  - step → image, input/output model versions, declared secrets, mapper paths, handler bundle hash
    (from the esbuild metafile, so imported shared code counts)
  - stage placement / `when` wrapping are *pipeline* changes, not step changes
- A model change ripples to every step consuming it.
- Hash the **manifest**, not the source; describe must be deterministic (no env/time/network) or
  versions flap. Lockfile changes legitimately bump every handler — the GUI labels it "dependency
  change".
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
- Paused runs (waiting for continue/promote, braked, waiting steps) don't block draining and become
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

- **Pipeline view**: stages, field-level wiring from mapper paths / model matches, `when` blocks,
  versions with diff.
- **Trigger form** generated from the input model; **run plan** preview (greyed-out stages whose
  `when` won't match).
- **Run view**: input, then each step's attempts with typed input/output rendered against the pinned
  model versions; skip reasons; superseded/rerun links.
- **Wait resolution form** generated from the wait step's output model.
- **Health summary** redefined over runs/stages — `Skipped` never counts as unhealthy.
- `pl`: `trigger --input k=v`, `run continue`, `step rerun [--continue]`, `pipeline switch <version>`,
  `pipeline manifest` (replaces `pipeline document`), and **local runs**:
  `pl run --step build --input sha=…` (wanted early — the handler dev loop depends on it).

## 8. Security

- The describe Job and step Jobs run under the existing runner hardening (gVisor, PSS baseline,
  NetworkPolicy). New egress needs: describe needs the repo + npm registry; the init container needs
  Garage.
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
| Poll trigger (`$SECRET:` headers) | `.onPoll(…, mapper)` |
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
- **Arbitrary-payload webhook mapping** for wait callbacks (run as a one-off Job).
- **SDK publishing** (`@olve/pipelines` distribution and version compatibility with the service).
- **TypeSpec** (or other) model sources — possible later since JSON Schema is the interchange.

## 12. Spikes (before building on them)

1. **Harness in any image**: compiled Bun executable + bundled libc/loader runs in glibc, Alpine and
   kaniko (libc-less) images under gVisor.
2. **One binary, many modes**: `bun build --compile` of models + pipeline + handlers with `describe`,
   `run`, `check`, `map-trigger`; deterministic manifest; per-handler bundle hashes from the metafile.
3. **Describe sandbox**: describe Job fetching repo + deps under gVisor/NetworkPolicy, emitting the
   manifest to the service.
4. **Type-level builder**: `Ref<T>` mappers, availability accumulation, maybe-available outputs from
   `when`, readable error messages.

## 13. Transition plan

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
