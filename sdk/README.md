# @olivervea/pipelines

The [Olve.Pipelines](https://github.com/OliverVea/Olve.Pipelines) v2 SDK. It has four parts:

- the **authoring API**: models, steps, handlers and the typed pipeline builder;
- the **step harness**: the runtime compiled into each pipeline's single executable (`describe`, `run`, `map-trigger`);
- the **build tool** (`olve-build`), which turns a pipeline directory into `step.tar.zst` and `manifest.json`;
- the **dev runner** (`olve-dev`), which runs the same harness path from source (used by `pl dev`).

Design: [`docs/design/pipelines-v2.md`](https://github.com/OliverVea/Olve.Pipelines/blob/main/docs/design/pipelines-v2.md). Spike findings: [`pipelines-v2-spikes.md`](https://github.com/OliverVea/Olve.Pipelines/blob/main/docs/design/pipelines-v2-spikes.md).
The manifest format is in [`manifest.schema.json`](manifest.schema.json), generated from [`src/describe/manifest-schema.ts`](src/describe/manifest-schema.ts).

```
sdk/
  src/index.ts            authoring API (`@olivervea/pipelines`)
  src/core/               models, secrets, config, steps, implement()
  src/builder/            pipeline builder (types + runtime records), Ref<T>
  src/library/            library steps (gitHubSource, imageBuild, helmDeployOverSsh, helmDeployInCluster, composeDeploy), their exported helpers (httpHealthCheck, sshSession, checkSpa) and SDK models; public API only
  src/describe/           describe: manifest, checks, mapper recording + source analysis, manifest schema
  src/harness/            harness CLI modes, env contract, envelope, result upload (`@olivervea/pipelines/harness`)
  src/ctx/                the ctx handed to handlers
  src/testing/            testHandler (`@olivervea/pipelines/testing`)
  src/build/              olve-build
  src/dev/                olve-dev
  examples/self-deploy/   fixture pipeline (the self-deploy shape) used by the tests
  tests/compile-fail/     type errors that must happen (checked by scripts/check-compile-fail.ts)
  tests/compile-pass/     type-level assertions that must hold
  tests/runtime/          bun tests
```

## Install

The package is `@olivervea/pipelines` on npm; a pipeline directory's `package.json` depends on it by that name:

```json
{
  "type": "module",
  "dependencies": { "@olivervea/pipelines": "2.0.0-beta.3" },
  "devDependencies": { "@types/bun": "1.4.3", "typescript": "7.0.2" }
}
```

```sh
bun install                    # commit bun.lock: the build Job installs with --frozen-lockfile
bunx olve-dev describe .
bunx olve-build . --out .olve/out
```

- The package ships TypeScript sources (Bun runs them directly; `harness.sdkHash` hashes them), so the
  same version gives the same `sdkHash` and handler hashes from npm or from `file:../sdk`.
- Inside this repo, `.pipelines/` and `.pipelines-test/` use `"@olivervea/pipelines": "file:../sdk"`.

### Releases

Versions are `2.0.0-beta.N` on the `beta` dist-tag (`publishConfig`) until v2 reaches prod, then
`latest`. To release, bump `version` in `package.json`, push to `v2`: once the run reaches prod, the
self-deploy's `publish-sdk` step runs `bun run check` and `bun publish` when that version is not on npm yet. It
skips when the version exists, or when the pipeline secret `NPM_TOKEN` is unset. Published versions: `npm view @olivervea/pipelines versions`.

## Authoring

A pipeline directory (the binding path, `.pipelines/` by default) looks like this:

```
models.ts                 data models                         (contract file)
pipeline.ts               steps + `export default pipeline(…)` (contract file)
handlers/*.ts             one per custom step: `export default implement(step, fn)` (pairs by the step, not the file name)
handlers/lib/…            shared helpers (not handlers)
scripts/*.sh              embedded with `import s from "../scripts/x.sh" with { type: "file" }`
```

Contract files hold contracts only. Describe evaluates them without loading handlers, and they are kept out of every handler's hash.

### Models, secrets, steps

```ts
import { model, z, fileRef, optional, external, secret, step, steps, Source } from "@olivervea/pipelines";

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
  input: { artifact: BuildArtifact },          // model | optional(model) | plain zod schema | external(model, …)
  output: BetaDeployment,                      // a model, or omitted (no output)
  secrets: [SSH_PRIVATE_KEY],                  // only these are mounted; ctx.secrets is typed to them
  timeoutSeconds: 900,                         // optional
  retries: 1,                                  // optional: a failed attempt is retried (fresh pod) up to n times
});
```

Rules:

- Models are **nominal**: two models with the same shape are not interchangeable.
- There is **one schema per model name**. Two different schemas registered under the same name is a describe error.
- **Outputs are validated strictly**: unknown keys are rejected at any depth, not stripped.
- **Trigger mappers** produce `z.input`, so fields with defaults may be left out. **Handlers, mapper refs and `when`** see `z.output`.
- A **plain-schema input** has no model to match on, so the step needs a mapper.
- **`optional(M)`** means "maybe skipped": `M` must still have an earlier producer.
- **`external(M, { title, description?, from? })`** is answered from outside the run; `from` lists `callback()` (another system POSTs to the URL an earlier step gets from `ctx.inputUrl(step, input)`), `poll({ url, valuePath, intervalSeconds? })` and `person()` (the default). It needs no producer and no mapper entry; the step waits until it is answered, then the value (validated against `M`) is in its input like any other. Failure handlers can't have one.

### Config

Values that differ per deployment (hosts, namespaces, which identity provider) are **config**: typed entries the pipeline declares, whose values live on the instance (one YAML document per pipeline, edited with `pl config` or the dashboard), not in the repo.

```ts
import { config, secret, secretRef, step, z } from "@olivervea/pipelines";

export const DEX_CLIENT_SECRET = secret("DEX_CLIENT_SECRET", "Dex client secret.");
export const AUTH = config("auth", "How users authenticate.", z.discriminatedUnion("mode", [
  z.object({ mode: z.literal("none") }),
  z.object({ mode: z.literal("dex"), issuer: z.string().url(), clientSecret: secretRef(DEX_CLIENT_SECRET) }),
]));
export const TARGET = config("target", "Where the release is deployed.",
  z.object({ host: z.string(), namespace: z.string().default("olve-pipelines") }));

export const deploy = step({ name: "deploy", description: "Deploys the release.", image: "alpine:3.22",
  config: [AUTH, TARGET],              // ctx.config is typed to exactly these
  secrets: [DEX_CLIENT_SECRET] });     // a secretRef's secret must be declared too
```

```yaml
# the instance's config document for this pipeline
auth: { mode: dex, issuer: https://dex.example.com, clientSecret: { secret: DEX_CLIENT_SECRET } }
target: { host: deploy@box }
```

- `config(name, description, schema)` mirrors `secret(...)`. Names match `^[a-z][a-zA-Z0-9]*$` (they are YAML keys).
- An entry whose schema accepts `undefined` (`.optional()`, `.default(…)`) is optional; every other entry is required. Defaults are applied when config is read, never written into the document.
- Handlers see `ctx.config` with the output view (defaults applied); a discriminated union narrows as usual.
- `secretRef(S)` is a field whose value is `{ secret: "<S.name>" }`: it names a secret, never holds its value. The handler reads the value from `ctx.secrets`. A step that receives an entry containing a `secretRef` must declare that secret.
- Config is per **run**: the run pins a config snapshot at start; continues and step reruns reuse it. Secrets are per attempt.
- Library steps (and any step's `settings`) take **config references** in place of values: `host: TARGET.host`, `target: TARGET.beta`. A reference is a typed proxy (`ConfigRef<T>`, `T` = the field's output type; `Configurable<T> = T | ConfigRef<T>`), so a `number` field or an optional field can't go where a `string` is required. Fields named `kind`, `name`, `description` or `schema` (the entry's own properties) are reached through `$` (`TARGET.$.name`); `TARGET.$` and `TARGET` itself reference the whole entry. The step's `config` gets every entry referenced in its `settings` (`configEntries` in the manifest); the manifest records each reference as `{"$config": "<entry>.<path>"}` (field names must not contain `.`; literal keys starting with `$` are reserved); the handler resolves them with `resolveConfigRefs(value, ctx.config)` (defaults applied). `describe` and reconcile reject a reference to an entry the step doesn't receive or a field the entry doesn't have.
- `clusterAccess: true` on a step asks for the instance's step ServiceAccount token (`Kubernetes:StepServiceAccount`; manifest `clusterAccess: true`, omitted otherwise). Without the instance setting the pod gets no token, like every step.

### Library steps

Library steps carry their own implementation and image; they take typed settings and use the SDK models `Source`, `BuiltImage`, `PushedImage` and `HelmRelease`. Each is a function returning `implement(step({ kind, settings, … }), handler)`, built on the public API only (a test enforces it), so a step package outside the SDK is written the same way (see [Reusable steps](#reusable-steps)). The set grows from real use: a step or an option exists because several pipelines use it; what one pipeline needs stays a custom step there.

| Step | Image | Input → output | Config |
|---|---|---|---|
| `steps.gitHubSource` | `alpine:3.22` (work happens in the harness) | `{ repo: "owner/name", sha }` (plain schemas: needs a mapper) → `Source` | `token?: Secret` (sent as `Authorization: Bearer` when set; unset = anonymous; becomes the step's secret), `apiUrl?` (default `https://api.github.com`) |
| `steps.imageBuild` | `gcr.io/kaniko-project/executor:debug` | `{ source: Source }` → `BuiltImage { repository, tag, digest, tar }`; with `push`: `PushedImage { repository, tag, digest }` | `repository` (image name; tag = run version, `-attempt<n>` on reruns), `context?` (dir in the source tree with the `Dockerfile`; default the root), `push?` (push to `repository` instead of a tarball), `contentTag?` (with `push`: tag `src-<context hash>`, skip when the registry has it), `registryMap?` (kaniko `--registry-map`: registry → mirror prefix; default none), `stallTimeoutSeconds?` (default 300, 0 = off), `retries?` (default 2), `registryAuth?` (`{ username, password: Secret }`: log in to the `repository` registry; or a config reference to `{ username, password: { secret: NAME } }`, with `NAME` in `secrets`), `insecureRegistry?` (that registry is plain HTTP), `secrets?` (declared secrets a config `registryAuth` may name), `enabled?` (`false` skips the step) |
| `steps.helmDeployOverSsh` | `alpine:3.22` (+ `apk add openssh-client` at run time) | `{ source: Source, <key>: BuiltImage }` per `images` (`PushedImage` with `fromRegistry`) → `HelmRelease { release, namespace, image? }` (`image`: the first image; absent without images) | `host` (`user@host`), `sshKey: Secret`, `release`, `namespace`, `chart` (dir in the source), `values?` (chart-relative `-f`), `set?`, `images?` (input key → values prefix; default `{ image: "image" }`; `{}` = the chart alone), `fromRegistry?`, `imageRefs?` (`PushedImage` input key → values prefix: set as `<prefix>.repository`/`.tag`/`.digest`, not pulled; works next to tarball `images`), `deployments?` (default `[release]`, none without images), `rolloutTimeoutSeconds?` (default 120), `dependencies?` (pull `Chart.yaml`'s dependencies on the host), `helmWait?` / `atomic?` (`helm --wait` / `--atomic`), `healthUrl?` (URL or `{ url, attempts?, delaySeconds? }`) |
| `steps.helmDeployInCluster` (and the `target` form of `helmDeployOverSsh`) | `alpine/helm:3.16.2` (ssh: `alpine:3.22`) | `{ source: Source, <key>: PushedImage }` per `images`, `<key>: optional(PushedImage)` per `imageRefs` → `HelmRelease` (`image`: `repository:tag@digest` of the first image) | `target` (a `select` case's reference to a `helmTarget()` variant: `{ kind: "inCluster", namespace, release, values, set, url? }` for helmDeployInCluster, the same plus `host` with `kind: "ssh"` for helmDeployOverSsh; `url` is where step pods reach the release, default its in-cluster Service URL, and is passed on in `HelmRelease.url`), `sshKey: Secret` (ssh only), `chart`, `inlineValues?` (objects written as values files after the target's), `images?`, `imageRefs?`, `deployments?` (ssh), `rolloutTimeoutSeconds?` (default 120), `healthPath?`; helmDeployInCluster declares `clusterAccess` |
| `steps.composeDeploy` | `alpine:3.22` (work happens in the harness) | `{ image: PushedImage }` → `HelmRelease { release: <service>, namespace: "compose", image: <repository>@<digest> }` | `target` (a `select` case's reference to the compose variant of `deployTarget()`: `{ kind: "compose", deployerUrl, service, token: { secret }, healthUrl? }`), `token: Secret` (must be the secret the target names; the step's only secret), `timeoutSeconds?` |

- `gitHubSource` downloads `<apiUrl>/repos/<repo>/tarball/<sha>` (retrying 5xx), strips the top-level directory and outputs it as `tree`.
- `imageBuild` copies the tree to `/kaniko/build-context` (kaniko wipes other directories between multi-stage builds), builds `--context=/kaniko/build-context[/<context>] --dockerfile=<that>/Dockerfile` with `--no-push --tar-path --digest-file`, and outputs the tarball. A missing context or Dockerfile fails the step before kaniko starts. `repository` in the output is fully qualified the way a runtime names a loaded image (`x` → `docker.io/library/x`). A **stall watchdog** kills kaniko when it prints nothing for `stallTimeoutSeconds` (a base-image pull can stall on an idle connection at "Unpacking rootfs", and kaniko has no read timeout), failing the attempt; `retries` then starts a fresh attempt. The retry is deliberately a new pod, never a second kaniko in the same container: kaniko unpacks the base image over the container root and does not clean up when killed.
- **Caches never block a build.** `registryMap` maps registries to pull-through caches (e.g. `{ "mcr.microsoft.com": "mcr-cache.example.com" }`; kaniko under gVisor can stall pulling from mcr.microsoft.com directly, a nearby cache doesn't). There is none by default. Before kaniko starts, every mirror host gets a 5 s `GET https://<host>/v2/` (200 or 401 = up); one that doesn't answer is dropped with a log line (`<cache> unreachable; pulling from <registry> directly`). After that, kaniko itself copes: it falls back to the original registry when a mirror can't serve an image's manifest (`RetrieveRemoteImage`; `--skip-default-registry-fallback` is never set). A cache that dies mid-download (after the manifest came from it) fails or stalls the attempt; `retries` starts a fresh one, which probes again.
- **Registry mode** (`push: true`, for images too big for the step's ~1 GiB scratch space and Garage): kaniko runs without `--no-push`/`--tar-path`, pushes to `repository` (it must start with a registry host; anonymous unless `registryAuth`) and the output is a `PushedImage` with the digest from `--digest-file`. With `contentTag`, the tag is `src-` + the first 12 hex of the sha256 of the context directory's deterministic tar (the bytes `ctx.output.dir` would write), computed from the fetched tree; a `HEAD https://<host>/v2/<path>/manifests/<tag>` (answering a Basic or Bearer challenge: anonymous token, or the `registryAuth` login; an anonymous lookup that is denied counts as "not there") that answers a `Docker-Content-Digest` skips kaniko and outputs that digest. With `registryAuth`, the step declares the password secret and writes `$DOCKER_CONFIG/config.json` (`/kaniko/.docker` in the kaniko image) for that registry before kaniko runs, so pushes and base-image pulls from it are logged in. `insecureRegistry` makes the lookups plain HTTP and adds kaniko `--insecure --insecure-registry=<host>`.
- `helmDeployOverSsh` gets each image into the host's **k3s** containerd: a `BuiltImage` tar is streamed in (`nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load`), a `PushedImage` (`fromRegistry`) is pulled there (`… pull -q`, so the rollout doesn't wait on the pull). It copies the chart to `/tmp/<release>-helm-<namespace>`, runs `helm upgrade --install` from inside it (values, then `set`, then per image `<prefix>.repository`/`.tag`/`.pullPolicy` — `Never` for tarballs, `IfNotPresent` for registry images — which always win), waits for `kubectl rollout status` of each of `deployments` in order (`deploy/<name>` for a bare name), and with `healthUrl` polls it (5 × 5 s unless given) from the harness. `images: {}` deploys the chart alone: the step's only input is `source`, no image is imported and no image flag set, and nothing is waited on unless `deployments` names it. `dependencies: true` runs `helm pull <name> --repo <repository> --version <version>` (or the `oci://` reference) into the copied chart's `charts/` for each dependency in `Chart.yaml`; `helmWait` / `atomic` add `--wait` / `--atomic` with `--timeout=<rolloutTimeoutSeconds>s` and make `deployments` default to none. Several images (one chart, two Deployments):

  ```ts
  steps.helmDeployOverSsh({ name: "deploy-beta", description: "", host: "oliver@bulwark-m2", sshKey: SSH_PRIVATE_KEY,
    release: "olve-slimerepublics", namespace: "apps-beta", chart: "helm",
    images: { server: "image", web: "web.image" }, deployments: ["olve-slimerepublics-server", "olve-slimerepublics-web"] });
  ```

- **Deploy targets.** `helmTarget(defaults?, extend?)` is the `inCluster | ssh` union. `deployTarget({ namespace?, release?, extend?, composeToken, optional? })` adds `compose` (`{ deployerUrl, service, token: secretRef(composeToken), healthUrl?, …extend }`) and, with `optional: true`, `none` (`{ kind: "none" }`, no other fields; its `select` case usually has no steps). A library step that references a target narrowed to one case needs only that case's secrets: the helm steps don't declare the compose `token`.
- `composeDeploy` resolves its target, checks `kind` is `compose` and that `token.secret` is the declared secret, then `POST <deployerUrl>/v1/deploy` `{ service, image: "<repository>@<digest>" }` with `Authorization: Bearer <token>`. Each line of the answer's `log` is logged as `deployer: …`; anything but HTTP 2xx with `status: "deployed"` fails the step with the deployer's status and message. With `healthUrl` it then runs `httpHealthCheck` (12 × 5 s). The deployer itself (`compose/deployer`) and its API: [docs/setup/compose-deploy.md](../docs/setup/compose-deploy.md).

**Helpers.** General functions the library steps are built on, exported for custom handlers:

| Helper | What it does |
|---|---|
| `httpHealthCheck(ctx, url, { attempts?, delaySeconds?, headers? })` | GETs `url` until it returns 2xx (redirects followed), retrying network errors and non-2xx statuses; defaults 5 attempts, 5 s apart. Throws the last error when the attempts run out. Runs in the harness, so the image needs no curl. `helmDeployOverSsh`'s `healthUrl` uses it. |
| `serviceUrl(release)` | `http://<release>.<namespace>.svc.cluster.local` for a `HelmRelease` (charts that name the Service after the release). |
| `checkSpa(ctx, url)` | `GET <url>/` must reference an `/assets/*.js` bundle (Vite's layout) and the bundle must be served as JavaScript; returns `{ html, bundle }` for app-specific checks. |
| `sshSession(ctx, { host, key })` | Installs openssh-client when the image has no ssh (apk or apt-get), writes the key to `<tmpDir>/ssh/id` (trailing newline, mode 600; host key not verified) and returns `{ run(remote), text(remote), copy(local, remote), shell(remote) }`; `shell` is the quoted ssh command for pipes (`ctx.sh(`printf '%s' "$V" \| ${s.shell("kubectl apply -f -")}`)`). `helmDeployOverSsh` uses it. |

```ts
import { httpHealthCheck, implement } from "@olivervea/pipelines";
export default implement(smoke, async (_, ctx) => { await httpHealthCheck(ctx, "http://app.ns.svc.cluster.local/api/health", { attempts: 10 }); });
```

### Reusable steps

A step several pipelines use is a function returning `implement(step(…), handler)`, staged directly: it carries its handler, so it has no `handlers/` file. The library steps are written this way, so a module in a repo or a package of its own works the same.

```ts
import { implement, resolveConfigRefs, serviceUrl, step, HelmRelease, type Configurable, type LiteralName } from "@olivervea/pipelines";

export function smokeTest<const N extends string>(def: { name: LiteralName<N>; description: string; path: Configurable<string> }) {
  return implement(
    step({ name: def.name, description: def.description, kind: "smokeTest", image: "alpine:3.22", input: { release: HelmRelease }, settings: { path: def.path } }),
    async ({ release }, ctx) => void (await ctx.http.expectOk(`${release.url ?? serviceUrl(release)}${resolveConfigRefs(def.path, ctx.config)}`)),
  );
}
```

- **`kind`**: a label, required for a step that carries its implementation (`custom` is a step with a handler file). Prefix a package's kinds (`acme/smokeTest`).
- **`settings`**: what the step was set up with, recorded as the step's manifest `config` (so part of its version). JSON values and config references; the step receives every entry its settings reference.
- **Identity**: `implement` and `step` record the module that called them (from the stack; a tail call can name a module further up, which only adds to what is hashed). The build bundles those modules, minus `pipeline.ts` and `models.ts`, like a handler, and the kind's hash (`kindHashes` in the generated `handlers.ts`) goes into each such step's `handlerHash`. A step that carries its implementation and has no other module (defined and implemented in a contract file) is a build error.
- `implement` refuses a step that already carries an implementation; a `handlers/*.ts` file for such a step is a describe error.

### Handlers

```ts
// handlers/deploy-beta.ts
import { implement } from "@olivervea/pipelines";
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
| `sh(cmd \| script, { env, cwd, args })`, `sh.text(…)` | Runs a command string, or an embedded script, through the image's shell. The script is copied to `$OLVE_DIR/tmp/scripts/` first. The shell is the first of `/bin/sh`, `/busybox/sh`, `/bin/bash` that exists; a shell-less image gets a clear `ShellNotFoundError`. A script's shebang is honoured when its interpreter exists. A string counts as an embedded script when it is a `/$bunfs/…` path, or an absolute path to an existing `*.sh` or `*.bash` file. Pass `{ script }` to force script mode. `args` are the script's arguments, or `$1`, `$2`… of a command string. A non-zero exit throws a `CommandError` that carries `exitCode`. |
| `exec([argv…], { env, cwd, idleTimeoutSeconds })` | Runs a binary directly, with no shell (works on distroless). `idleTimeoutSeconds` (also on `sh`) is an inactivity watchdog: no stdout/stderr output for that long → SIGTERM, SIGKILL 10 s later, and a `CommandStalledError`. |
| `readText(path)`, `readJson(path)` | Paths are relative to the cwd. |
| `files.fetch(ref)` | Downloads a file reference and verifies its sha256. Directory refs are extracted. Returns a local path. |
| `output.write(name, content)`, `output.file(path, { name })`, `output.dir(path, { name })` | Adds a file to the step's output and returns its `FileRef`. A directory becomes a deterministic tar. |
| `http.fetch`, `http.getJson(url, { headers })`, `http.expectOk(url, { method, headers })` | Bound to `ctx.signal`. A non-2xx status throws a readable error, with the URL redacted after `?`. |
| `retry({ attempts, delaySeconds, backoff? }, fn)` | Retries `fn`. Can be cancelled. |
| `secrets.NAME` | Typed to the step's declared secrets. Read from the env var `NAME`. Throws if the variable is missing. |
| `config.<entry>` | Typed to the step's declared config entries (output view, defaults applied). From input.json's `$config`. An optional entry that is not set is `undefined`. |
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
- **`select` (branch on config).** `.select(TARGET.prod, { ssh: (p, prod) => p.stage(…), inCluster: (p, prod) => p.stage(…) })`: the first argument references a `z.discriminatedUnion` in a config entry (`DiscriminatorOf<U>` is its one literal field; a union with no or several literal fields is a type error). Cases are exhaustive and checked at compile time; each gets the reference narrowed to its variant (`ConfigNode<VariantOf<U, D, V>>`), recorded as `{"$config": "target.prod", "$case": "target.prod.kind=ssh"}` and usable only inside that case. The manifest records `{select, discriminator, cases}`; the service resolves it per run from the pinned config snapshot (`when` is per run input). The same step name may appear in several cases (same output model; not two different custom steps, since handlers pair by name); a step every case has is definitely available afterwards, one only some have is maybe-available. The harness picks the variant of a same-named step from `OLVE_CASES` (a JSON array of the case conditions the step sits in, set by the service).
- **Retention.** `pipeline({ …, retention: { keepLatestRuns?, minRunAgeDays?, buildGraceDays? } })` overrides the instance's retention thresholds for this pipeline's runs and binaries, field by field (non-negative integers; unset = the instance's value). Recorded on the manifest root as `retention`; the service applies the active version's.
- **Failure handlers.** `.onFailure(handler, { steps? })` takes a step whose inputs are all `FailureContext`. When a run fails, the service runs each handler whose scope (default: any step) contains a failed step, once per failure: `run <handler>` in the handler's image with its declared secrets, from the run's pinned binary. It is untracked and best-effort — not an attempt of the run, never retried, and its own failure triggers nothing; its log is kept at `runs/<p>/<run>/_failure/<handler>/<id>/log`. `FailureContext` carries `pipeline {id, name}`, `run {id, number, address, versionStamp, trigger, url}`, `version {number, commit}`, the run's `input`, a one-line `reason`, and `failedSteps[]` (`step, attempt, address, reason, logUrl`) — only the steps in the handler's scope, never empty.

### Testing handlers

```ts
import { testHandler, testFileRef } from "@olivervea/pipelines/testing";
import deployBeta from "../handlers/deploy-beta";

const r = await testHandler(deployBeta, { artifact }, { secrets: { SSH_PRIVATE_KEY: "k" }, config: { target: { host: "h" } }, ctx: { http: fakeHttp }, run: { number: 412 } });
expect(r).toMatchObject({ status: "succeeded", output: { release: "olve-pipelines" } });   // r.filesDir, r.logs, r.exitCode
```

`testHandler` uses the same validation path as a real run. Invalid input gives `status: "failed"` with `kind: "input"`; an invalid return value gives `kind: "output"`. Any part of `ctx` can be replaced; whatever you don't replace is the real implementation. `testHandler` takes any implementation: a handler module's default export, a library step or a reusable step.

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
| `input.json` | service (via olve-init) | **run**: a JSON object with exactly the step's input keys, built from the manifest's `input` mappings. Leave out a key whose ref root step was skipped; this is allowed only for `optional` inputs. Unknown keys → exit 65. For a step with `configEntries`, the reserved key `"$config"` holds `{ <entry>: <value> }` from the run's config (entries that are not set are left out); each entry is parsed with its schema, and a missing required entry or an invalid value → exit 65 (`config.<entry>…`). Other steps ignore `$config`. **map-trigger push**: `{"ref": "refs/heads/<branch>", "after": "<sha>", "repository": {"full_name": "<owner>/<repo>"}}` (`PushEvent`). **map-trigger poll**: `{"value": "<extracted value as string>"}`. **map-trigger webhook**: `{"headers": {…}, "body": …}` (`WebhookEvent`). |
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
| `OLVE_CASES` | run | Only for a step inside a `select`: a JSON array of the case conditions it sits in (`["target.prod.kind=ssh"]`); `run <step>` uses it to pick the variant of a name several cases use. |
| `<SECRET_NAME>` | per step | Each declared secret as an env var of the **same name**: `secretKeyRef` key = name. Mount only the step's `secrets` (for poll triggers, the poll's `secrets`). Child processes inherit them. The mount is optional: an unset secret leaves the variable unset and `ctx.secrets.<NAME>` throws on access, so a handler can check `process.env.<NAME>` first and skip. |
| `OLVE_RESULT_URL` | optional | Pre-signed **PUT** for `result.tar.gz` (see below). Unset means no upload. |
| `OLVE_CALLBACK_URLS` | optional | JSON object mapping `<step>/<input>` → this run's callback URL of that external input (`ctx.inputUrl`). Set when the pipeline has `callback()` inputs and the instance has a public base URL. |
| `OLVE_FILE_URLS` | optional | JSON object mapping file key → pre-signed **GET** URL, one entry per file ref in the input. The key is `<run>/<step>/<attempt>/<path>` (`path` exactly as in the ref, including a trailing `/` for directories). The URL points at the stored object (see the storage rule). |
| `OLVE_FILES_BASE_URL` (+ `OLVE_FILES_TOKEN`) | optional | Fallback: `GET <base>/<run>/<step>/<attempt>/<object path>` with an optional `Authorization: Bearer <token>`. |
| `OLVE_FILES_DIR` | dev | Local pool laid out as `<run>/<step>/<attempt>/files/<object path>`. |

`OLVE_RESULT_URL`, `OLVE_FILE_URLS`, `OLVE_FILES_TOKEN` and `OLVE_CALLBACK_URLS` are removed from the environment of child processes. URLs are never logged past `?`.

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
| 65 | `input.json` missing, not JSON, or invalid for the step (or event), including its `$config` | failed / `input` |
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
- **Step `kind`** is `custom` (a handler file) or the kind of a step that carries its implementation (`imageBuild`, `composeDeploy`, a package's `acme/…`). To the service it is a label: any non-empty kind is accepted, so a new library or package step needs no service change.
  - `output` is the `z.output` view, with `additionalProperties: false` on every object. It validates outputs, the pool and trigger results.
  - `input` is the `z.input` view (defaults optional). Use it for trigger forms and manual input. It is emitted only when it differs from `output` beyond `additionalProperties`.
- **Step `input`** maps each input key to a mapping:
  - `{"ref": "<root>[.<field>…]"}`, where the root is `input`, a step name, or `failure` (failure handlers only);
  - `{"const": <json>}`;
  - a nested object of mappings.

  An object whose only key is `ref` (with a string value) or `const` is a tag. Describe never emits a nested object with exactly that key set. Model-matched inputs are emitted already resolved (`{"ref": "build"}`).
- **Step `externalInputs`** (when present) maps each external input key to `{model, title, description, from}`, `from` being `{kind: "person"}`, `{kind: "callback"}` or `{kind: "poll", url, valuePath, intervalSeconds}`. Those keys are not in `input` or `inputTypes`; the service merges the answers into `input.json`.
- **`inputTypes`** declares each input key: `{model, optional}`, or `{schema: {output, input?}}` for a plain schema.
- **`handlerHash`** is the step's implementation identity. A step version can be computed from image + `inputTypes`/model versions + `output` model version + `secrets` + `configEntries` (and those entries' schemas) + `input` mappings + `handlerHash`. Stage placement, `when` and the step's `description` are not part of it.
  - Custom steps: `sha256(canonical {bundle, harness})`. `bundle` hashes the handler bundled on its own, with `pipeline.ts`, `models.ts` and `@olivervea/pipelines` external. It is tree-shaken and includes embedded scripts, so it changes only when that handler's code (or code it uses) changes.
  - Steps that carry their implementation: `sha256(canonical {library: kind, config, harness, code})`, `code` being the hash of the modules that define and implement steps of that kind (see [Reusable steps](#reusable-steps)).
  - Because `harness` is part of both hashes, a Bun or SDK bump changes every handler.
- **`stages`** is an array whose items are either `{steps: [Step…]}` (parallel, with a barrier) or `{when: {field: [values]}, stages: [...]}`.
- **`triggers`**:
  - `{kind: "push", name: "push"}`;
  - `{kind: "poll", name, url, headers, secrets, valuePath, intervalSeconds}`, where header values may contain `$SECRET:NAME`;
  - `{kind: "webhook", name, verify: {scheme: "hmac-sha256", secret, header, prefix} | {scheme: "bearer", secret}}`. The `map-trigger` event is `{headers, body}`; a `null` mapping yields a `skipped` envelope (the delivery is ignored).
- **`failureHandlers`** is a list of `{step: Step, steps: [names] | null}`. Each handler's inputs are all FailureContext (`{"ref": "failure"}`).
- **`retention`** (when present) is `{keepLatestRuns?, minRunAgeDays?, buildGraceDays?}`, non-negative integers that replace the instance's thresholds for this pipeline. Omitted when the definition sets none.
- **`secrets`** lists every secret used by steps, polls and webhooks, sorted, as `{name, description}`.
- **`config`** lists every config entry steps declare, sorted by name, as `{name, description, required, schema}`. `schema` is the `z.input` view (defaults listed); it validates the entry's value in the config document. `required` is false when the schema accepts a missing value. A `secretRef` field is marked `"x-olve-kind": "secretRef"`, with the secret's name as `properties.secret.const`. Omitted when there are no entries.
- **Step `configEntries`** names the entries the step receives (sorted; omitted when none). It is part of the step's content, like `secrets`. The step key `config` is the step's `settings`.

Describe runs these checks; reconcile should re-run the manifest-level ones:

- step name format and reserved names (`input`, `failure`), and duplicate step names;
- one schema per model name, and JSON-representable models;
- missing handler file, a handler file without a step (or for a step that carries its implementation), and a step that carries its implementation without a kind;
- availability, ambiguity, maybe-available as required, `optional` without a producer, plain schema without a mapper;
- mapper source analysis and recording, unknown fields in refs, mapper keys that are missing or extra;
- `when` on an unknown or non-literal field, or with an invalid value;
- a poll header referencing an undeclared secret, duplicate triggers;
- conflicting secret descriptions;
- config names, one description and schema per config name, a step receiving a `secretRef` without declaring its secret, and input keys starting with `$` (reserved);
- an `onFailure` scope naming an unknown step, and failure-handler inputs.

## Build tool

```
olve-build <pipelineDir> --out <dir> [--target bun-linux-x64|bun-linux-x64-musl] [--upload-url <presigned PUT>] [--sentinels]
olve-build <pipelineDir> --hash-only          # print handler hashes (diagnostics)
```

1. Pair each `handlers/*.ts` with the step its `implement(step, …)` names, and hash it (see `handlerHash`); hash the modules of each kind of step that carries its implementation.
2. Generate `<pipelineDir>/.olve/gen/handlers.ts` (a registry of literal `import()` paths, plus hashes and harness info) and `entry.ts`. Add `.olve/` to `.gitignore`.
3. Run `bun build --compile --target=bun-linux-x64 --no-compile-autoload-dotenv --no-compile-autoload-bunfig --bytecode --format=esm` → `<out>/step`. The output is never minified, because mapper source analysis needs real source.
4. Check that `PT_INTERP` is `/lib64/ld-linux-x86-64.so.2`. On alpine, Bun silently produces a musl binary; the check fails it.
5. Run `<out>/step describe` → `<out>/manifest.json`. A describe problem is a build failure with exit 78. **A custom step without a handler file is an error.**
6. Write `<out>/step.tar.zst` (a deterministic tar, `step` at the root, mode 0755, zstd level 3: it unpacks in the step pod far faster than gzip; harness-base also still unpacks the gzip archives of older SDKs; from 2.0.0-beta.7 the SDK needs an instance whose harness-base unpacks zstd, i.e. one deployed on or after 2026-10-25) and `<out>/build.json` (diagnostics).
7. With `--upload-url`, PUT `step.tar.zst`, using the same retry rules as the result upload. With `--sentinels`, print the manifest between the manifest sentinels.

Exit codes: 0 ok, 1 build failure, 64 usage, 78 pipeline invalid.

**Build/describe Job recipe**, on `oven/bun:<pinned>-slim` (never alpine):

```sh
# fetch the repo tarball, then in the binding path's package root:
bun install --frozen-lockfile
bunx olve-build .pipelines --out /work/out --upload-url "$STEP_PUT_URL" --sentinels
```

From npm, nothing else is needed. A local `file:`/`link:` dependency (`"@olivervea/pipelines": "file:../sdk"`, as Olve.Pipelines' own `.pipelines/` does) resolves to its **source directory**, not the copy in `node_modules`, so its own dependencies must be installed there too: the build Job runs `bun install --frozen-lockfile --production` in every local dependency of the binding's `package.json` that is inside the repo, before building.

The repo depends on the SDK as a normal package. It resolves through `exports` (`@olivervea/pipelines`, `@olivervea/pipelines/harness`, `@olivervea/pipelines/testing`), and the tests cover this with a `file:` install. `bun` must be on `PATH`, because the bins use `#!/usr/bin/env bun`. A consumer `tsconfig.json` has to include the dot-directory explicitly (`"include": [".pipelines/**/*.ts"]`), and should exclude `.pipelines/.olve`.

How the service runs it (reconcile, `BuildJobBuilder`): one Job per (pipeline, config commit), named `olve-build-<pipeline12>-<sha12>`. The repo tarball comes from `codeload.github.com/<repo>/tar.gz/<sha>` anonymously (public repos), or from the GitHub API tarball endpoint with the binding's credentials key from the pipeline Secret. The archive is PUT to `versions/<pipeline>/builds/<sha>/step.tar.gz` (the key predates zstd; the init container tells the formats apart by content), and a version added from that build keeps this key as its `BinaryKey`. The in-repo fixture `.pipelines-test/` (bound on v2 beta) uses exactly this layout.

The manifest is the text between the sentinels in the pod log. The manifest text is identical across checkouts. The binary and archive bytes are identical for the same checkout layout.

## Dev runner

```
olve-dev describe <dir> [--sentinels]                     # same bytes as the compiled binary's describe
olve-dev run <dir> <step> [--input-file f] [--config-file f] [--olve-dir d] [--secret NAME=value]...
olve-dev map-trigger <dir> <trigger> --event-file f [--olve-dir d]
```

`olve-dev` runs handlers from source, through the same validation path as the binary. `$OLVE_DIR` defaults to `<dir>/.olve`, and outputs land in `<dir>/.olve/work/`. Set `OLVE_FILES_DIR` to a local pool to fetch file refs. `--config-file` (a JSON object of config entries) becomes the input's `$config`.

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
