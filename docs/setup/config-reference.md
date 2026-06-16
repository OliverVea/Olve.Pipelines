# `config.yaml` Reference

[← Index](index.md) · [Subject Index](subjects.md)

The complete schema for `.pipelines/config.yaml` — every field, the file-extraction mechanics
(`$ref`, `scriptFile`), secret declarations, triggers, and the exact validation rules the
server enforces on reconcile.

`<path>` (the config directory) defaults to `.pipelines`. The server fetches the whole subtree
under it, so `config.yaml`, `steps/*.yaml`, and `scripts/*.sh` are all available for resolution.

## Top-level schema

```yaml
apiVersion: "0.0"                 # REQUIRED. "major.minor"; major must match the server.
name: my-app                      # REQUIRED. The pipeline name.
description: Build and deploy.    # optional, free-form
version: "1"                      # optional, free-form (your own versioning, not the API version)

secrets:                          # optional. Declared by NAME ONLY (values never in repo).
  - name: GITHUB_TOKEN
    description: Read token to fetch the repo tarball.

productionSteps:                  # optional. Run in PARALLEL → bundle/<step-name>/.
  - name: build
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh

processingSteps:                  # optional. Run SEQUENTIALLY (order = list position).
  - name: deploy
    configuration:
      image: alpine:latest
      script: |
        echo deploying...

triggers:                         # OPTIONAL & additive (the deploy poll is implicit).
  - name: redeploy
    target: { type: processing, processingStepName: deploy }
```

### `apiVersion`

`"major.minor"` (e.g. `"0.0"`). The **major** must equal the server's current major (currently
`0`). A mismatched major **rejects the whole reconcile**. The minor is informational. Empty or
non-`major.minor` strings are rejected.

### `name`

Required, non-empty. The pipeline's display name.

## Steps

Both `productionSteps` and `processingSteps` are lists of step objects. They share the same
shape; the only difference is execution semantics:

| | Production | Processing |
|---|---|---|
| Concurrency | all in parallel | sequential, in list order |
| Output | writes to `bundle/<step-name>/` | receives the full ArtifactBundle |
| Promotion gate | none | yes (brake + re-promote) — see [Promotion Gate](promotion-gate.md) |

### Step object

```yaml
- name: build                     # REQUIRED. Unique within its list.
  configuration:
    image: <container-image>       # REQUIRED.
    script: |                      # REQUIRED (unless scriptFile). The script the job runs.
      ...
    scriptFile: scripts/build.sh   # alternative to script (mutually exclusive).
    environmentVariables:          # optional. Map of name → value.
      LOG_LEVEL: debug
      TOKEN: $SECRET:GITHUB_TOKEN  # $SECRET:NAME resolves from the pipeline's k8s secret.
```

`environmentVariables` values may reference declared secrets with `$SECRET:NAME`. Plain
declared secrets are also mounted into every job as ordinary env vars, so a script can read
`$GITHUB_TOKEN` directly without listing it in `environmentVariables`.

## File extraction: `$ref` and `scriptFile`

For larger configs you can split steps and scripts into their own files under the config
directory. Resolution happens on reconcile, before validation.

### `$ref` — pull a step from its own file

Replace a step element with a single `$ref` to a YAML file containing the step object:

```yaml
productionSteps:
  - $ref: steps/build.yaml        # steps/build.yaml contains { name, configuration }
```

Rules:
- A `$ref` step **must not set any other field** — `{ $ref: ..., name: ... }` is rejected.
- The referenced file must exist in the config subtree and be a YAML mapping.

### `scriptFile` — keep a script out-of-line

Inside a step's `configuration`, use `scriptFile` instead of an inline `script`:

```yaml
configuration:
  image: alpine:latest
  scriptFile: scripts/deploy.sh
```

Rules:
- `script` and `scriptFile` are **mutually exclusive** — setting both is rejected.
- The referenced file must exist in the config subtree.
- On resolution the file's contents are inlined as the step's `script`.

## Secrets

Declared by **name only**. Values never live in the repo — they are stored in the pipeline's
own Kubernetes secret (`olve-pipeline-{id}`) and injected directly into jobs by Kubernetes;
they never pass through the app at runtime.

```yaml
secrets:
  - name: GITHUB_TOKEN
    description: Read token to fetch the repo tarball.    # optional, surfaced in status
```

Reference a secret in two ways:

- **`$SECRET:NAME`** inside an env var value or a poll trigger header.
- **As a plain env var** (`$GITHUB_TOKEN`) inside a script — every declared secret is mounted
  into every job.

You set values via the operational endpoint (works even on a bound pipeline):

```http
PUT /api/pipelines/{id}/secrets/{name}
```

> **Validation:** every `$SECRET:NAME` referenced in env values or poll headers **must** be
> declared in `secrets:`, or the whole reconcile is rejected.

## Triggers

**Optional and additive.** Binding already configures the deploy poll, so you do **not** declare
a deploy trigger. Use `triggers:` only for *extra* automation. There are three target types:

```yaml
triggers:
  # Fire all production steps (a fresh build).
  - name: rebuild
    target: { type: production }

  # Fire one processing step with the latest artifact bundle.
  - name: redeploy
    target: { type: processing, processingStepName: deploy }

  # Poll an upstream URL; trigger production when a JSON value changes.
  - name: upstream-version
    target:
      type: poll
      url: https://api.example.com/latest
      valuePath: data.version          # dot-path into the JSON response
      intervalSeconds: 60              # optional, default 60
      headers:                         # optional; values may use $SECRET:NAME
        Authorization: "Bearer $SECRET:UPSTREAM_TOKEN"
```

| `type` | Fields | Behavior |
|---|---|---|
| `production` | — | Triggers all production steps. |
| `processing` | `processingStepName` | Triggers that processing step with an artifact bundle. The step name **must** exist in `processingSteps`, or reconcile is rejected. |
| `poll` | `url`, `valuePath`, `intervalSeconds?`, `headers?` | Background poller GETs `url`, extracts the value at `valuePath` (dot-path), and triggers production when it changes. Header values support `$SECRET:NAME`. |

## Validation rules (reconcile rejects on any of these)

The server compiles the file tree, resolves `$ref`/`scriptFile`, deserializes, then validates.
The **whole reconcile is rejected** (live state unchanged) if any of these fail:

| Rule | Failure message shape |
|---|---|
| `config.yaml` present in the config dir | `Config file 'config.yaml' not found …` |
| `config.yaml` is a YAML mapping | `'config.yaml' must be a YAML mapping.` |
| Valid YAML | `Failed to parse 'config.yaml': …` |
| `name` non-empty | `Config 'name' is required.` |
| `apiVersion` major matches server | `ApiVersion '…' is incompatible: expected major 0 …` |
| `apiVersion` in `major.minor` form | `ApiVersion '…' is not in 'major.minor' form.` |
| Production step names unique | `Duplicate production step name '…'.` |
| Processing step names unique | `Duplicate processing step name '…'.` |
| `processing` trigger references a real step | `Trigger '…' references unknown processing step '…'.` |
| Every `$SECRET:NAME` is declared | `Secret '$SECRET:NAME' is referenced but not declared in 'secrets:'.` |
| `$ref` step sets no other fields | `Step '$ref: …' must not set any other fields.` |
| `$ref` / `scriptFile` target exists | `Referenced step file '…' not found.` / `Script file '…' not found.` |
| Not both `script` and `scriptFile` | `Step configuration sets both 'script' and 'scriptFile' …; use one.` |

A rejected reconcile **holds off the build** for that cycle — see
[config-before-build](binding-and-reconcile.md#config-before-build). The problems are surfaced
in `GET /api/pipelines/{id}/binding/status`.

## Worked example — this repo's own config

Olve.Pipelines dogfoods the feature. Its
[`.pipelines/config.yaml`](https://github.com/OliverVea/Olve.Pipelines/blob/main/.pipelines/config.yaml)
builds with Kaniko and deploys beta-then-prod with Helm (beta gates prod because processing
steps are sequential):

```yaml
apiVersion: "0.0"
name: olve-pipelines
description: Build with Kaniko, deploy to the homelab (beta then prod) with Helm.
version: "1"

secrets:
  - name: GITHUB_TOKEN
    description: GitHub read token used to fetch the repo tarball during build.
  - name: SSH_PRIVATE_KEY
    description: SSH key for importing the image and running helm on the homelab host.

productionSteps:
  - name: build-and-package
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh

processingSteps:
  - name: deploy-beta
    configuration:
      image: alpine:latest
      scriptFile: scripts/deploy-beta.sh
  - name: deploy
    configuration:
      image: alpine:latest
      scriptFile: scripts/deploy.sh
```
