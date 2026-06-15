# Typed step templates — design spec

**Date:** 2026-06-15
**Status:** Design draft → ready for review
**Repo:** OliverVea/Olve.Pipelines

## Goal

Give users **easy, safe** building blocks for the common build/deploy steps so they
don't hand-write the same Kaniko/SSH/Helm shell boilerplate (and its quoting and
homelab plumbing) in every repo. A step's `configuration:` can be replaced by a
typed, parameterized `template:` that the service **expands to the existing
`(image, script, environmentVariables)`** at compile time. Raw `script:` /
`scriptFile:` stays as the always-available escape hatch — anything a template
can't express, the user writes by hand.

**Closed set (decided 2026-06-15):** templates are **server-defined** in v1. No
user-contributed or remote templates — the user is always free to drop to a full
script instead. This keeps expansion trusted and validated, and defers the
trust/sandbox/registry questions entirely.

This builds on the in-repo GitOps config feature
([2026-06-14-in-repo-pipeline-config-design.md](2026-06-14-in-repo-pipeline-config-design.md)):
`.pipelines/config.yaml` → `ManifestCompiler` → `PipelineManifest` → `PipelineDocument`.

## Why not a CDK / npm SDK (for the record)

AWS CDK / Pulumi earn their weight when the config surface is huge and needs loops,
conditionals, and abstraction — an imperative language pays for itself. This
manifest is ~40 lines; the pain is **repetition + safety of common steps**, not
expressiveness. Templates solve that directly with no new runtime, build step, or
codegen layer. A CDK can always emit this YAML later without any server change, so
deferring it costs nothing. Out of scope here; revisit only if declarative hits a
real wall.

## Key design decision — expansion is a `ManifestCompiler` concern

Templates expand to `StepConfigurationDocument(image, script, env)` **during
compile**, immediately after `$ref` / `scriptFile` resolution and before
`PipelineManifest` is deserialized into a `PipelineDocument`. Consequences:

- **Nothing downstream changes.** `PipelineDocument`, `PipelineReconciler`, the
  job executor, the S3 snapshot, and the diff all keep seeing plain
  `(image, script, env)`. A templated step and an equivalent hand-written step
  reconcile and execute identically.
- Templates are pure functions `params → StepConfigurationDocument`. No I/O, no
  state — testable in isolation with golden-file expansion tests.
- AOT-safe: the template set is a closed `[JsonPolymorphic]` hierarchy with
  `System.Text.Json` source-gen, exactly like the existing trigger-target
  deserializer.

## Scope

In v1:

- A step may carry **either** `configuration:` (raw, today's shape) **or**
  `template:` (typed) — mutually exclusive, validated.
- Closed template set: **`kaniko-build`** and **`helm-deploy`** — the two the live
  self-deploy pipeline already uses, so the expansions are proven by the current
  hand-written scripts.
- Per-template parameter validation returning `Olve.Results` problems (reuses the
  whole-reconcile-rejection path).
- Shell-safe interpolation: every parameter value spliced into a generated script
  is escaped (single-quote shell-quoting) — typed params must not become an
  injection vector.
- Golden-file tests: each template + params → expected `(image, script, env)`.

Out of v1 (keep the schema extensible):

- **User-contributed / open templates**, remote `$ref`, a registry, or
  `uses: org/name@version`. Closed set only; escape hatch is the full script.
- **CDK / SDK.** See above.
- **Cross-step variable passing** beyond the artifact bundle. Data still flows
  step→step via files in the bundle (e.g. `version.txt`), as today.
- **Non-homelab deploy targets.** v1 `helm-deploy` encodes the homelab's
  registry-less "build image tar → import into k3s containerd via SSH+nerdctl →
  helm upgrade" model. A registry-push build + standard pull-based helm deploy are
  future templates, not v1.
- `dotnet-build` (Dockerfile-free .NET build) — a likely third template; deferred
  until `kaniko-build` + `helm-deploy` are proven.

---

## Manifest schema

A step is `{ name, configuration? , template? }` with exactly one of
`configuration` / `template` set. `template` is polymorphic on `type`:

```yaml
productionSteps:
  - name: build-and-package
    template:
      type: kaniko-build
      repo: OliverVea/Olve.Pipelines     # default: the bound repo
      branch: main                        # default: the bound branch
      dockerfile: Dockerfile              # default: Dockerfile
      destination: olve-pipelines         # image name (tag is the generated version)
      artifacts: [helm]                   # dirs copied from context into the bundle

processingSteps:
  - name: deploy-beta
    template:
      type: helm-deploy
      host: oliver@bulwark-m2             # SSH target that runs nerdctl/helm
      release: olve-pipelines
      namespace: apps-beta
      chart: helm                         # chart dir within the input bundle
      valuesFile: helm/values-beta.yaml   # optional extra -f values
      set:                                # optional --set overrides
        slo.enabled: "false"
      healthCheck:                        # optional gate; failure stops the chain
        url: https://pipelines-beta.ovea.pro/api/health
        retries: 5
  - name: deploy
    template:
      type: helm-deploy
      host: oliver@bulwark-m2
      release: olve-pipelines
      namespace: apps
      chart: helm
      set: { slo.enabled: "false" }
```

This is the current `.pipelines/config.yaml` self-deploy pipeline expressed with
templates — replacing ~80 lines of three shell scripts with declarative params,
while the generated `(image, script, env)` is byte-for-byte the proven scripts.

### Built-in template variables

A tiny **closed** set of substitutions, no expression evaluation:

- `kaniko-build` generates the version (`date +%Y%m%d-%H%M%S`), writes
  `version.txt` into the bundle, and tags the image `destination:<version>`.
- `helm-deploy` reads `version.txt` from the input bundle (`/input/*/`) for the
  image tag. Cross-step flow is the bundle — unchanged from today.

No user-facing `{{var}}` syntax in v1; the variables are internal to each
template's expansion.

## Template contracts (expansion targets)

### `kaniko-build` → production step

- Image: `gcr.io/kaniko-project/executor:debug`.
- Script: fetch repo tarball (busybox wget, `Authorization: token $GITHUB_TOKEN`),
  copy `artifacts` dirs + `version.txt` to `/output/`, run `/kaniko/executor`
  against `dockerfile` with `--no-push --tar-path=/output/image.tar`. Context at
  `/kaniko/build-context` (survives multi-stage wipes), no `--single-snapshot`.
- Params: `repo`, `branch`, `dockerfile`, `destination`, `context?`, `artifacts[]`.
- Implicit secret dependency: `GITHUB_TOKEN` (declared in `secrets:` as today).

### `helm-deploy` → processing step

- Image: `alpine:latest` (+ `apk add openssh-client`, `curl` if `healthCheck`).
- Script: write `$SSH_PRIVATE_KEY`, `ssh-keyscan` the host, glob the input bundle
  dir, import `image.tar` into k3s containerd via
  `nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load`,
  scp the `chart`, `helm upgrade --install` with `image.tag=<version>`,
  `image.pullPolicy=Never`, any `valuesFile` (`-f`) and `set` (`--set`) overrides;
  optional rollout wait + `healthCheck` retry loop that exits non-zero on failure
  (gating the next step).
- Params: `host`, `release`, `namespace`, `chart`, `valuesFile?`,
  `set{}?`, `imageRepository?` (default `docker.io/library/<release>`),
  `healthCheck{url,retries}?`.
- Implicit secret dependency: `SSH_PRIVATE_KEY`.

## Safety

- **Closed set** → expansions are authored and reviewed in-repo; no arbitrary
  remote code.
- **Validated params** → wrong/missing params fail the whole reconcile with a
  precise problem, before anything runs.
- **Escaped interpolation** → every param value spliced into a generated script is
  single-quote shell-escaped. This is the one real injection surface and the spec
  treats it as mandatory, with a test per template that feeds a `'; rm -rf`-style
  value and asserts it lands inert.
- **Escape hatch unchanged** → raw `script:`/`scriptFile:` still does anything;
  templates never remove capability, only remove boilerplate for the common path.

## Implementation sketch

- `Pipelines/Sync/Templates/IStepTemplate.cs`: `Result<StepConfigurationDocument> Expand()`.
- `StepTemplate` abstract record, `[JsonPolymorphic("type")]` +
  `[JsonDerivedType(KanikoBuildTemplate, "kaniko-build")]` /
  `(HelmDeployTemplate, "helm-deploy")`; add to a source-gen context like
  `ManifestJsonContext`.
- `ProductionStepDocument` / `ProcessingStepDocument` gain an optional
  `Template`; `ManifestCompiler` validates one-of and replaces `Template` with the
  expanded `Configuration` before deserializing to `PipelineDocument`.
- A small `ShellQuote` helper used by every template's script builder.
- Golden-file tests under `test/Olve.Pipelines.UnitTests` (expansion + injection
  inertness); rewrite this repo's `.pipelines/config.yaml` to templates as the
  end-to-end proof once the set lands.

## Open questions

- Should `template` allow an additive `env:` merge (extra vars on top of the
  template's), or stay strictly one-of? Leaning: allow extra `env` merge, since
  it's safe and common; keep `script` strictly one-of.
- `set` value typing — YAML scalars arrive as strings (camelCase + string-number
  handling already configured); confirm `--set` formatting for bools/numbers.
- Do we want `kaniko-build.destination` to default to the pipeline/release name to
  cut one more param?
