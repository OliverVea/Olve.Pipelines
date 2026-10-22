# @olivervea/pipelines

The [Olve.Pipelines](https://github.com/OliverVea/Olve.Pipelines) v2 SDK, imported as `@olve/pipelines`. It has four parts:

- the **authoring API**: models, steps, handlers and the typed pipeline builder;
- the **step harness**: the runtime compiled into each pipeline's single executable (`describe`, `run`, `map-trigger`);
- the **build tool** (`olve-build`), which turns a pipeline directory into `step.tar.gz` and `manifest.json`;
- the **dev runner** (`olve-dev`), which runs the same harness path from source (used by `pl dev`).

Design: [`docs/design/pipelines-v2.md`](https://github.com/OliverVea/Olve.Pipelines/blob/v2/docs/design/pipelines-v2.md). Spike findings: [`pipelines-v2-spikes.md`](https://github.com/OliverVea/Olve.Pipelines/blob/v2/docs/design/pipelines-v2-spikes.md).
The manifest format is in [`manifest.schema.json`](manifest.schema.json), generated from [`src/describe/manifest-schema.ts`](src/describe/manifest-schema.ts).

```
sdk/
  src/index.ts            authoring API (`@olve/pipelines`)
  src/core/               models, secrets, steps, implement()
  src/builder/            pipeline builder (types + runtime records), Ref<T>
  src/library/            library steps (script, gitHubSource, imageBuild, helmDeployOverSsh), their exported helpers (httpHealthCheck) and SDK models
  src/describe/           describe: manifest, checks, mapper recording + source analysis, manifest schema
  src/harness/            harness CLI modes, env contract, envelope, result upload (`@olve/pipelines/harness`)
  src/ctx/                the ctx handed to handlers
  src/testing/            testHandler (`@olve/pipelines/testing`)
  src/build/              olve-build
  src/dev/                olve-dev
  examples/self-deploy/   fixture pipeline (the self-deploy shape) used by the tests
  tests/compile-fail/     type errors that must happen (checked by scripts/check-compile-fail.ts)
  tests/compile-pass/     type-level assertions that must hold
  tests/runtime/          bun tests
```

## Install

The package is `@olivervea/pipelines` on npm; pipelines import it as `@olve/pipelines` through an alias
in the pipeline directory's `package.json`:

```json
{
  "type": "module",
  "dependencies": { "@olve/pipelines": "npm:@olivervea/pipelines@2.0.0-beta.1" },
  "devDependencies": { "@types/bun": "1.4.3", "typescript": "7.0.2" }
}
```

```sh
bun install                    # commit bun.lock: the build Job installs with --frozen-lockfile
bunx olve-dev describe .
bunx olve-build . --out .olve/out
```

- Keep the alias name: `olve-build` generates imports of `@olve/pipelines/harness`, so an unaliased
  `@olivervea/pipelines` dependency does not build.
- The package ships TypeScript sources (Bun runs them directly; `harness.sdkHash` hashes them), so the
  same version gives the same `sdkHash` and handler hashes from npm or from `file:../sdk`.
- Inside this repo, `.pipelines/` and `.pipelines-test/` use `"@olve/pipelines": "file:../sdk"`.

### Releases

Versions are `2.0.0-beta.N` on the `beta` dist-tag (`publishConfig`) until v2 reaches prod, then
`latest`. To release, bump `version` in `package.json` and push to `v2`: the self-deploy's
`publish-sdk` step runs `bun run check` and `bun publish` when that version is not on npm yet. It
skips when the version exists, or when the pipeline secret `NPM_TOKEN` is unset. Published versions: `npm view @olivervea/pipelines versions`.

## Authoring

A pipeline directory (the binding path, `.pipelines/` by default) looks like this:

```
models.ts                 data models                         (contract file)
pipeline.ts               steps + `export default pipeline(…)` (contract file)
handlers/<step-name>.ts   one per custom step: `export default implement(step, fn)`
handlers/lib/…            shared helpers (not handlers)
scripts/*.sh              embedded with `import s from "../scripts/x.sh" with { type: "file" }`
```

Contract files hold contracts only. Describe evaluates them without loading handlers, and they are kept out of every handler's hash.

### Models, secrets, steps

```ts
import { model, z, fileRef, optional, secret, step, steps, Source } from "@olve/pipelines";

export const BuildArtifact = model("BuildArtifact", z.object({
  version: z.string(),
  image: fileRef(),            // a file in some step attempt's output
  chart: fileRef(),            // a directory (path ends with "/")
}));
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for the host.");

export const deployBeta = step({
  name: "deploy-beta",                         // identity: lower-case kebab, ≤ 63 chars
  description: "Deploys to beta.",             // required (may be ""); shown in the UI, not versioned
  image: "alpine:latest",                      // any image; the harness brings its own runtime
  input: { artifact: BuildArtifact },          // model | optional(model) | plain zod schema
  output: BetaDeployment,                      // a model, or omitted (no output)
  secrets: [SSH_PRIVATE_KEY],                  // only these are mounted; ctx.secrets is typed to them
  timeoutSeconds: 900,                         // optional
  retries: 1,                                  // optional: a failed attempt is retried (fresh pod) up to n times
});

import testSh from "./scripts/test.sh" with { type: "file" };
export const test = steps.script({ name: "test", description: "Runs the tests.", image: "…", script: testSh, input: { source: Source }, output: TestReport,
  env: { TAG: ({ source }, run) => `${run.version}-${source.sha.slice(0, 7)}` } });   // env values may be functions of the inputs
```

Rules:

- Models are **nominal**: two models with the same shape are not interchangeable.
- There is **one schema per model name**. Two different schemas registered under the same name is a describe error.
- **Outputs are validated strictly**: unknown keys are rejected at any depth, not stripped.
- **Trigger mappers** produce `z.input`, so fields with defaults may be left out. **Handlers, mapper refs and `when`** see `z.output`.
- A **plain-schema input** has no model to match on, so the step needs a mapper.
- **`optional(M)`** means "maybe skipped": `M` must still have an earlier producer.

**`steps.script` contract** (for the script):

| Name | What it holds |
|---|---|
| `$OLVE_INPUT` | Path to the validated input as JSON. |
| `INPUT_<KEY>` | One variable per input key. `deployTo` becomes `INPUT_DEPLOY_TO`. Strings are passed raw; everything else is JSON. |
| `$OLVE_OUTPUT` | Set only when the step has an output model. Write the output JSON to this path. |

In the output JSON, an object of the form `{"$file": "<path>"}` or `{"$dir": "<path>"}` becomes a file reference. The step's declared secrets are environment variables named after the secrets.

### Library steps

Library steps carry their own implementation and image; they take typed config and use the SDK models `Source`, `BuiltImage` and `HelmRelease`. The set grows from real use: a step or an option exists because a pipeline uses it.

| Step | Image | Input → output | Config |
|---|---|---|---|
| `steps.script` | yours | yours | `script` (embedded), `env` (see above) |
| `steps.gitHubSource` | `alpine:3.22` (work happens in the harness) | `{ repo: "owner/name", sha }` (plain schemas: needs a mapper) → `Source` | `token?: Secret` (sent as `Authorization: Bearer`; becomes the step's secret) |
| `steps.imageBuild` | `gcr.io/kaniko-project/executor:debug` | `{ source: Source }` → `BuiltImage { repository, tag, digest, tar }` | `repository` (image name; tag = run version, `-attempt<n>` on reruns), `cacheRepo?` (kaniko layer cache), `registryMap?` (kaniko `--registry-map`: registry → mirror prefix, falls back upstream), `stallTimeoutSeconds?` (default 300, 0 = off), `retries?` (default 2) |
| `steps.helmDeployOverSsh` | `alpine:3.22` (+ `apk add openssh-client` at run time) | `{ source: Source, image: BuiltImage }` → `HelmRelease { release, namespace, image }` | `host` (`user@host`), `sshKey: Secret`, `release`, `namespace`, `chart` (dir in the source), `values?` (chart-relative `-f`), `set?`, `healthUrl?` |

- `gitHubSource` downloads `https://api.github.com/repos/<repo>/tarball/<sha>` (retrying 5xx), strips the top-level directory and outputs it as `tree`.
- `imageBuild` copies the tree to `/kaniko/build-context` (kaniko wipes other directories between multi-stage builds), builds `Dockerfile` at the tree root with `--no-push --tar-path --digest-file`, and outputs the tarball. `repository` in the output is fully qualified the way a runtime names a loaded image (`x` → `docker.io/library/x`). A **stall watchdog** kills kaniko when it prints nothing for `stallTimeoutSeconds` (a base-image pull can stall on an idle connection at "Unpacking rootfs", and kaniko has no read timeout), failing the attempt; `retries` then starts a fresh attempt. The retry is deliberately a new pod, never a second kaniko in the same container: kaniko unpacks the base image over the container root and does not clean up when killed.
- `helmDeployOverSsh` streams the image tar into the host's **k3s** containerd (`nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load`), copies the chart to `/tmp/<release>-helm-<namespace>`, runs `helm upgrade --install` from inside it (values, then `set`, then `image.repository`/`image.tag`/`image.pullPolicy=Never`, which always win), waits for `kubectl rollout status deploy/<release>`, and with `healthUrl` polls it (5 × 5 s) from the harness.

**Helpers.** General functions the library steps are built on, exported for custom handlers:

| Helper | What it does |
|---|---|
| `httpHealthCheck(ctx, url, { attempts?, delaySeconds?, headers? })` | GETs `url` until it returns 2xx (redirects followed), retrying network errors and non-2xx statuses; defaults 5 attempts, 5 s apart (v1's deploy-beta loop). Throws the last error when the attempts run out. Runs in the harness, so the image needs no curl. `helmDeployOverSsh`'s `healthUrl` uses it. |

```ts
import { httpHealthCheck, implement } from "@olve/pipelines";
export default implement(smoke, async (_, ctx) => { await httpHealthCheck(ctx, "http://app.ns.svc.cluster.local/api/health", { attempts: 10 }); });
```

### Handlers

```ts
// handlers/deploy-beta.ts
import { implement } from "@olve/pipelines";
import { deployBeta } from "../pipeline";
import deployScript from "../scripts/deploy.sh" with { type: "file" };

export default implement(deployBeta, async ({ artifact }, ctx) => {
  await ctx.sh(deployScript, { env: { IMAGE_TAR: await ctx.files.fetch(artifact.image), CHART_DIR: await ctx.files.fetch(artifact.chart) } });
  await ctx.retry({ attempts: 5, delaySeconds: 5 }, () => ctx.http.expectOk("https://pipelines-beta.ovea.pro/api/health"));
  return { release: "olve-pipelines", version: artifact.version, url: "https://pipelines-beta.ovea.pro" };   // or ctx.skip("reason")
});
```

| `ctx.` | |
|---|---|
| `sh(cmd \| script, { env, cwd, args })`, `sh.text(…)` | Runs a command string, or an embedded script, through the image's shell. The script is copied to `$OLVE_DIR/tmp/scripts/` first. The shell is the first of `/bin/sh`, `/busybox/sh`, `/bin/bash` that exists; a shell-less image gets a clear `ShellNotFoundError`. A script's shebang is honoured when its interpreter exists. A string counts as an embedded script when it is a `/$bunfs/…` path, or an absolute path to an existing `*.sh` or `*.bash` file. Pass `{ script }` to force script mode. A non-zero exit throws a `CommandError` that carries `exitCode`. |
| `exec([argv…], { env, cwd, idleTimeoutSeconds })` | Runs a binary directly, with no shell (works on distroless). `idleTimeoutSeconds` (also on `sh`) is an inactivity watchdog: no stdout/stderr output for that long → SIGTERM, SIGKILL 10 s later, and a `CommandStalledError`. |
| `readText(path)`, `readJson(path)` | Paths are relative to the cwd. |
| `files.fetch(ref)` | Downloads a file reference and verifies its sha256. Directory refs are extracted. Returns a local path. |
| `output.write(name, content)`, `output.file(path, { name })`, `output.dir(path, { name })` | Adds a file to the step's output and returns its `FileRef`. A directory becomes a deterministic tar. |
| `http.fetch`, `http.getJson(url, { headers })`, `http.expectOk(url, { method, headers })` | Bound to `ctx.signal`. A non-2xx status throws a readable error, with the URL redacted after `?`. |
| `retry({ attempts, delaySeconds, backoff? }, fn)` | Retries `fn`. Can be cancelled. |
| `secrets.NAME` | Typed to the step's declared secrets. Read from the env var `NAME`. Throws if the variable is missing. |
| `log(message, fields?)` | Writes a `message k=v …` line to stderr. |
| `signal` | Aborted on SIGTERM/SIGINT. Running commands are sent SIGTERM. |
| `run` | `{ pipeline, id, number, attempt, step, version }` (from the env, see below). |
| `tmpDir` | A writable scratch directory (`$OLVE_DIR/tmp`). Kaniko has no `/tmp`. |
| `skip(reason)` | Produce no output (prefer `when`). |

### Pipeline

```ts
export default pipeline({ name: "Olve.Pipelines", description: "…", input: M.DeployRequest })
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after, deployTo: e.ref === "refs/heads/main" ? "prod" : "beta" }))
  .onPoll({ url: "https://api.github.com/repos/o/r/commits/main", secrets: [GITHUB_TOKEN],
            headers: { Authorization: "$SECRET:GITHUB_TOKEN" }, valuePath: "sha", intervalSeconds: 300 },
          value => ({ sha: value }))
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])   // [step, mapper] tuple
  .stage(build, test)                                                           // inputs matched by model
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(deployProd).when({ publishCli: true }, p => p.stage(publishCli)))
  .stage(summary)                                                               // optional(ProdDeployment) etc.
  .onFailure(aoeTriage, { steps: [deployProd] });
```

- **Stage forms.** `.stage(a)`, `.stage(a, b)`, `.stage([a, from => ({…})])` and `.stage([a, mapA], [b, mapB])` are accepted, and tuples can be mixed with plain steps. The old `.stage(step, mapper)` form is a type error and a describe error.
- **The `from` argument.** It is keyed by step name, plus `input` for the pipeline input. It holds `Ref<T>` values, so only property access and literals compile. Use `from["deploy-beta"].url` for names that contain hyphens.
- **Describe checks the mapper twice.** It records the mapper with proxies, and it statically analyses the mapper's source. The source must be an object literal of member chains, literals, constant arrays and nested object literals. Ternaries, `&&`, `||`, `??`, `!`, comparisons, arithmetic, template strings, calls, spread and optional chaining are all rejected. Free identifiers such as module constants are allowed; they are recorded as `{const}`.
- **Triggers.** A pipeline may call `onPush` at most once. Each `onPoll` and `onWebhook` needs a distinct `name` (`onPoll` defaults to `"poll"`). `onWebhook({ name, verify }, ({ headers, body }) => input | null)` maps a delivery to `/api/hooks/<pipeline id>/<name>`; `verify` is `hmacSha256({ secret, header, prefix? })`, `githubSignature(secret)` or `bearer(secret)`, and `null` ignores the delivery.
- **Failure handlers.** `.onFailure(handler, { steps? })` takes a step whose inputs are all `FailureContext`. When a run fails, the service runs each handler whose scope (default: any step) contains a failed step, once per failure: `run <handler>` in the handler's image with its declared secrets, from the run's pinned binary. It is untracked and best-effort — not an attempt of the run, never retried, and its own failure triggers nothing; its log is kept at `runs/<p>/<run>/_failure/<handler>/<id>/log`. `FailureContext` carries `pipeline {id, name}`, `run {id, number, address, versionStamp, trigger, url}`, `version {number, commit}`, the run's `input`, a one-line `reason`, and `failedSteps[]` (`step, attempt, address, reason, logUrl`) — only the steps in the handler's scope, never empty.

### Testing handlers

```ts
import { testHandler, testFileRef } from "@olve/pipelines/testing";
import deployBeta from "../handlers/deploy-beta";

const r = await testHandler(deployBeta, { artifact }, { secrets: { SSH_PRIVATE_KEY: "k" }, ctx: { http: fakeHttp }, run: { number: 412 } });
expect(r).toMatchObject({ status: "succeeded", output: { release: "olve-pipelines" } });   // r.filesDir, r.logs, r.exitCode
```

`testHandler` uses the same validation path as a real run. Invalid input gives `status: "failed"` with `kind: "input"`; an invalid return value gives `kind: "output"`. Any part of `ctx` can be replaced; whatever you don't replace is the real implementation. `testHandler` also accepts library steps.

## Harness contract (service ⇄ step binary)

This section is everything the C# side needs in order to run the binary.

### Invocation

```
/olve/lib/ld-linux-x86-64.so.2 --library-path /olve/lib /olve/step <mode> [arg]
```

| Mode | Reads | Writes | Notes |
|---|---|---|---|
| `describe [--sentinels]` | the binary itself | stdout: the manifest (canonical JSON) | With `--sentinels`, stdout gets the manifest between `---OLVE-MANIFEST-BEGIN---` and `---OLVE-MANIFEST-END---`. Needs no env and no files. |
| `run <step>` | `$OLVE_DIR/input.json` | `$OLVE_DIR/work/output.json`, `$OLVE_DIR/work/files/…` | Steps in stages and failure-handler steps. |
| `map-trigger <trigger>` | `$OLVE_DIR/input.json` (the event) | `$OLVE_DIR/work/output.json` | `<trigger>` is the trigger's `name` from the manifest. |

`run` and `map-trigger` also print the envelope on stdout, as one compact JSON line between `---OLVE-RESULT-BEGIN---` and `---OLVE-RESULT-END---`. The block always comes last. Use it as a fallback channel from the pod log. User output and stderr logs also go to the pod log, so never parse anything outside the sentinels.

### Files (`$OLVE_DIR`, default `/olve`)

| Path | Who | What |
|---|---|---|
| `input.json` | service (via olve-init) | **run**: a JSON object with exactly the step's input keys, built from the manifest's `input` mappings. Leave out a key whose ref root step was skipped; this is allowed only for `optional` inputs. Unknown keys → exit 65. **map-trigger push**: the GitHub push payload. **map-trigger poll**: `{"value": "<extracted value as string>"}`. |
| `work/output.json` | harness | The envelope. Any stale copy is deleted at start, and the new file is written atomically. |
| `work/files/<object path>` | harness | Output files and directory tars (see "File references"). |
| `tmp/` | harness | Scratch space. Point `TMPDIR` here. |
| `files.json` | service, optional | Same format as `OLVE_FILE_URLS`, for when the URL map is too large for an env var. |

### Environment

| Variable | Required | Meaning |
|---|---|---|
| `OLVE_DIR` | yes (default `/olve`) | IO root. |
| `TMPDIR` | recommended | `/olve/tmp`. |
| `OLVE_PIPELINE` | run | Pipeline id → `ctx.run.pipeline`. |
| `OLVE_RUN_ID` | run | Run id → `ctx.run.id`. |
| `OLVE_RUN` | run | Run number (integer ≥ 0). Written into produced file refs. Default 0. |
| `OLVE_ATTEMPT` | run | Attempt number (integer ≥ 1). Written into produced file refs. Default 1. |
| `OLVE_VERSION_STAMP` | run | Derived version, e.g. `2026.10.21.412` → `ctx.run.version`. |
| `<SECRET_NAME>` | per step | Each declared secret as an env var of the **same name**: `secretKeyRef` key = name. Mount only the step's `secrets` (for poll triggers, the poll's `secrets`). Child processes inherit them. The mount is optional: an unset secret leaves the variable unset and `ctx.secrets.<NAME>` throws on access, so a handler can check `process.env.<NAME>` first and skip. |
| `OLVE_RESULT_URL` | optional | Pre-signed **PUT** for `result.tar.gz` (see below). Unset means no upload. |
| `OLVE_FILE_URLS` | optional | JSON object mapping file key → pre-signed **GET** URL, one entry per file ref in the input. The key is `<run>/<step>/<attempt>/<path>` (`path` exactly as in the ref, including a trailing `/` for directories). The URL points at the stored object (see the storage rule). |
| `OLVE_FILES_BASE_URL` (+ `OLVE_FILES_TOKEN`) | optional | Fallback: `GET <base>/<run>/<step>/<attempt>/<object path>` with an optional `Authorization: Bearer <token>`. |
| `OLVE_FILES_DIR` | dev | Local pool laid out as `<run>/<step>/<attempt>/files/<object path>`. |

`OLVE_RESULT_URL`, `OLVE_FILE_URLS` and `OLVE_FILES_TOKEN` are removed from the environment of child processes. URLs are never logged past `?`.

`ctx.files.fetch` looks for a file in this order:

1. the attempt's own `work/files`;
2. `OLVE_FILE_URLS` / `files.json`;
3. `OLVE_FILES_DIR`;
4. `OLVE_FILES_BASE_URL`.

It retries network errors, 5xx, 408 and 429. It always verifies `sha256`.

### Envelope (`work/output.json`)

```json
{ "status": "succeeded", "output": { … } }        // output: the z.output value; null for steps without an output model
{ "status": "skipped",   "reason": "…" }
{ "status": "failed",    "error": { "kind": "handler" | "input" | "output", "message": "…" } }
```

For `map-trigger`, `output` is the pipeline input after it has been parsed by the input model, so defaults are applied. Validate it again server-side.

### Exit codes

| Code | Meaning | Envelope |
|---|---|---|
| 0 | ok (succeeded or skipped) | yes |
| 1 | the handler or trigger mapper threw | failed / `handler` |
| 64 | usage: unknown mode, step or trigger | none |
| 65 | `input.json` missing, not JSON, or invalid for the step (or event) | failed / `input` |
| 66 | the return value failed strict validation, or an own file ref is missing or changed | failed / `output` |
| 74 | the `result.tar.gz` upload failed after retries. Used only when the step itself succeeded; otherwise the step's own code wins. | yes (printed) |
| 78 | the pipeline is invalid (describe problems, or a missing or wrong handler module) | none |
| 130 / 143 | the handler failed after SIGINT / SIGTERM (cancellation, `activeDeadlineSeconds`) | failed / `handler` |

### Result upload (`OLVE_RESULT_URL`)

After writing the envelope, the harness packs `output.json` and `files/` into a deterministic `result.tar.gz`. It does this for **succeeded, skipped and failed** results alike. It then sends the archive with a single `PUT <OLVE_RESULT_URL>` and no extra headers.

- Network errors, 5xx, 408 and 429 are retried up to 5 times, with backoff of 1, 2, 4 and 8 s.
- Any other 4xx fails immediately.

The service unpacks the archive into `runs/<p>/<run>/<step>/<attempt>/`.

### File references

```json
{ "run": 412, "step": "build", "attempt": 1, "path": "chart/", "sha256": "<64 hex>" }
```

- In JSON Schema, a reference is marked `"x-olve-kind": "fileRef"`.
- `path` is relative, with no `.`/`..` segments, no `\`, no NUL and no leading `/`. The pattern is in the schema. A **trailing `/` means a directory**.
- **Storage rule:** the object path is `path` for files. For directories, it is `path` without the trailing `/`, plus `.tar`: `chart/` → `chart.tar`, an uncompressed deterministic tar.
- Objects live at `work/files/<object path>` in the pod and at `runs/<p>/<run>/<step>/<attempt>/files/<object path>` in Garage.
- `sha256` is the hash of the object (the tar, for directories).

## Manifest

Exact format: [`manifest.schema.json`](manifest.schema.json) (JSON Schema 2020-12, `$defs` `Manifest`, `Step`, `Stage`, `Mapping`, `Trigger`, `InputType`, `ModelSchemas`). The text is canonical JSON: sorted keys, 2-space indent, a trailing newline, and no `$schema` in the model schemas. Hash the text as printed.

- **`apiVersion`** is `"2.0"`.
- **`harness`** is `{ bun, sdk, sdkHash }`.
- **`models[name]`** is `{ output, input? }`.
  - `output` is the `z.output` view, with `additionalProperties: false` on every object. It validates outputs, the pool and trigger results.
  - `input` is the `z.input` view (defaults optional). Use it for trigger forms and manual input. It is emitted only when it differs from `output` beyond `additionalProperties`.
- **Step `input`** maps each input key to a mapping:
  - `{"ref": "<root>[.<field>…]"}`, where the root is `input`, a step name, or `failure` (failure handlers only);
  - `{"const": <json>}`;
  - a nested object of mappings.

  An object whose only key is `ref` (with a string value) or `const` is a tag. Describe never emits a nested object with exactly that key set. Model-matched inputs are emitted already resolved (`{"ref": "build"}`).
- **`inputTypes`** declares each input key: `{model, optional}`, or `{schema: {output, input?}}` for a plain schema.
- **`handlerHash`** is the step's implementation identity. A step version can be computed from image + `inputTypes`/model versions + `output` model version + `secrets` + `input` mappings + `handlerHash`. Stage placement, `when` and the step's `description` are not part of it.
  - Custom steps: `sha256(canonical {bundle, harness})`. `bundle` hashes the handler bundled on its own, with `pipeline.ts`, `models.ts` and `@olve/pipelines` external. It is tree-shaken and includes embedded scripts, so it changes only when that handler's code (or code it uses) changes.
  - Library steps: `sha256(canonical {library: kind, config, harness})`.
  - Because `harness` is part of both hashes, a Bun or SDK bump changes every handler.
- **`stages`** is an array whose items are either `{steps: [Step…]}` (parallel, with a barrier) or `{when: {field: [values]}, stages: [...]}`.
- **`triggers`**:
  - `{kind: "push", name: "push"}`;
  - `{kind: "poll", name, url, headers, secrets, valuePath, intervalSeconds}`, where header values may contain `$SECRET:NAME`;
  - `{kind: "webhook", name, verify: {scheme: "hmac-sha256", secret, header, prefix} | {scheme: "bearer", secret}}`. The `map-trigger` event is `{headers, body}`; a `null` mapping yields a `skipped` envelope (the delivery is ignored).
- **`failureHandlers`** is a list of `{step: Step, steps: [names] | null}`. Each handler's inputs are all FailureContext (`{"ref": "failure"}`).
- **`secrets`** lists every secret used by steps, polls and webhooks, sorted, as `{name, description}`.

Describe runs these checks; reconcile should re-run the manifest-level ones:

- step name format and reserved names (`input`, `failure`), and duplicate step names;
- one schema per model name, and JSON-representable models;
- missing handler file, and a handler file without a step (or for a library step);
- availability, ambiguity, maybe-available as required, `optional` without a producer, plain schema without a mapper;
- mapper source analysis and recording, unknown fields in refs, mapper keys that are missing or extra;
- `when` on an unknown or non-literal field, or with an invalid value;
- a poll header referencing an undeclared secret, duplicate triggers;
- conflicting secret descriptions;
- an `onFailure` scope naming an unknown step, and failure-handler inputs.

## Build tool

```
olve-build <pipelineDir> --out <dir> [--target bun-linux-x64|bun-linux-x64-musl] [--upload-url <presigned PUT>] [--sentinels]
olve-build <pipelineDir> --hash-only          # print handler hashes (diagnostics)
```

1. Hash every `handlers/*.ts` (see `handlerHash`).
2. Generate `<pipelineDir>/.olve/gen/handlers.ts` (a registry of literal `import()` paths, plus hashes and harness info) and `entry.ts`. Add `.olve/` to `.gitignore`.
3. Run `bun build --compile --target=bun-linux-x64 --no-compile-autoload-dotenv --no-compile-autoload-bunfig --bytecode --format=esm` → `<out>/step`. The output is never minified, because mapper source analysis needs real source.
4. Check that `PT_INTERP` is `/lib64/ld-linux-x86-64.so.2`. On alpine, Bun silently produces a musl binary; the check fails it.
5. Run `<out>/step describe` → `<out>/manifest.json`. A describe problem is a build failure with exit 78. **A custom step without a handler file is an error.**
6. Write `<out>/step.tar.gz` (deterministic, `step` at the root, mode 0755) and `<out>/build.json` (diagnostics).
7. With `--upload-url`, PUT `step.tar.gz`, using the same retry rules as the result upload. With `--sentinels`, print the manifest between the manifest sentinels.

Exit codes: 0 ok, 1 build failure, 64 usage, 78 pipeline invalid.

**Build/describe Job recipe**, on `oven/bun:<pinned>-slim` (never alpine):

```sh
# fetch the repo tarball, then in the binding path's package root:
bun install --frozen-lockfile
bunx olve-build .pipelines --out /work/out --upload-url "$STEP_PUT_URL" --sentinels
```

From npm (the alias), nothing else is needed. A local `file:`/`link:` dependency (`"@olve/pipelines": "file:../sdk"`, as Olve.Pipelines' own `.pipelines/` does) resolves to its **source directory**, not the copy in `node_modules`, so its own dependencies must be installed there too: the build Job runs `bun install --frozen-lockfile --production` in every local dependency of the binding's `package.json` that is inside the repo, before building.

The repo depends on the SDK as a normal package. It resolves through `exports` (`@olve/pipelines`, `@olve/pipelines/harness`, `@olve/pipelines/testing`), and the tests cover this with a `file:` install. `bun` must be on `PATH`, because the bins use `#!/usr/bin/env bun`. A consumer `tsconfig.json` has to include the dot-directory explicitly (`"include": [".pipelines/**/*.ts"]`), and should exclude `.pipelines/.olve`.

How the service runs it (reconcile, `BuildJobBuilder`): one Job per (pipeline, config commit), named `olve-build-<pipeline12>-<sha12>`. The repo tarball comes from `codeload.github.com/<repo>/tar.gz/<sha>` anonymously (public repos), or from the GitHub API tarball endpoint with the binding's credentials key from the pipeline Secret. The binary is PUT to `versions/<pipeline>/builds/<sha>/step.tar.gz`, and a version added from that build keeps this key as its `BinaryKey`. The in-repo fixture `.pipelines-test/` (bound on v2 beta) uses exactly this layout.

The manifest is the text between the sentinels in the pod log. The manifest text is identical across checkouts. The binary and archive bytes are identical for the same checkout layout.

## Dev runner

```
olve-dev describe <dir> [--sentinels]                     # same bytes as the compiled binary's describe
olve-dev run <dir> <step> [--input-file f] [--olve-dir d] [--secret NAME=value]...
olve-dev map-trigger <dir> <trigger> --event-file f [--olve-dir d]
```

`olve-dev` runs handlers from source, through the same validation path as the binary. `$OLVE_DIR` defaults to `<dir>/.olve`, and outputs land in `<dir>/.olve/work/`. Set `OLVE_FILES_DIR` to a local pool to fetch file refs.

## Development

```sh
bun install
bunx tsc --noEmit -p .                         # type-check (TS 7)
bun scripts/check-compile-fail.ts              # compile-fail cases (also run by bun test)
TSC=/path/to/tsc-5.9/bin/tsc bun scripts/check-compile-fail.ts   # the other TS line
bun test                                       # runtime + build/e2e tests
bun scripts/gen-manifest-schema.ts             # after changing src/describe/manifest-schema.ts
```

The type-level builder's inference is fragile; see the comments in `src/builder/pipeline.ts`. Keep the compile-fail checker green on both TypeScript lines.
