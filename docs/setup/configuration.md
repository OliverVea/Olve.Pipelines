# Pipeline Config

A pipeline describes *how* something is built and deployed. The values that differ per instance
(hosts, namespaces, domains, which identity provider to use) are **config**: the pipeline declares
typed config entries, and each instance stores the values in one YAML document per pipeline.

- [Declaring config](#declaring-config)
- [The config document](#the-config-document)
- [Config and secrets](#config-and-secrets)
- [Versions: pending until satisfied](#versions-pending-until-satisfied)
- [Runs pin their config](#runs-pin-their-config)
- [Defaults](#defaults)
- [Commands](#commands)

## Declaring config

```ts
import { config, secret, secretRef, step, z } from "@olivervea/pipelines";

export const DEX_CLIENT_SECRET = secret("DEX_CLIENT_SECRET", "Dex client secret for the server.");

export const AUTH = config("auth", "How users authenticate to the deployed instance.",
  z.discriminatedUnion("mode", [
    z.object({ mode: z.literal("none") }),
    z.object({
      mode: z.literal("dex"),
      issuer: z.string().url(),
      clientId: z.string(),
      clientSecret: secretRef(DEX_CLIENT_SECRET),
    }),
  ]));

export const TARGET = config("target", "Where the release is deployed.",
  z.object({ host: z.string(), namespace: z.string().default("olve-pipelines") }));

export const deploy = step({
  name: "deploy",
  description: "Deploys the release.",
  image: "alpine/helm:3",
  config: [AUTH, TARGET],          // like secrets: only declared entries are delivered
  secrets: [DEX_CLIENT_SECRET],    // a secretRef's secret must also be declared by the step
});
```

```ts
// handlers/deploy.ts
export default implement(deploy, async (_, ctx) => {
  const { host, namespace } = ctx.config.target;      // typed: exactly the step's entries
  if (ctx.config.auth.mode === "dex") ctx.log("dex", { issuer: ctx.config.auth.issuer });
  // ctx.secrets.DEX_CLIENT_SECRET holds the value the secretRef names
});
```

- `config(name, description, schema)`: `name` is a YAML key (`^[a-z][a-zA-Z0-9]*$`); `schema` is
  any zod schema JSON Schema can represent. Unions (`z.discriminatedUnion`) model alternatives.
- An entry whose schema accepts `undefined` (`.optional()`, `.default(…)`) is optional; every other
  entry is required.
- `secretRef(S)` is a field whose value in YAML is `{ secret: S }`: it names a secret, never holds
  its value.
- Checks (describe and reconcile): entry names; one schema and description per entry name; a step
  that receives an entry containing a `secretRef` must declare that secret (a library step: only the
  secrets of the parts it references); a `secretRef` must name a
  declared secret.
- A changed entry schema is a manifest change (a new pipeline version) and bumps the step versions
  of the steps that declare that entry.

### In library steps

Library steps take a config reference wherever they take a value (and so can your own reusable
steps, in their `settings`: [Reusable steps](pipeline-reference.md#reusable-steps)):

```ts
export const TARGET = config("target", "Where to deploy.", z.object({ beta: helmTarget(), prod: helmTarget() }));
export const PUBLISH = config("publish", "Where images go.", z.object({ repository: z.string() }));

export const build = steps.imageBuild({ name: "build", description: "", repository: PUBLISH.repository, push: true });
// select on a config union: each case gets the reference narrowed to its variant
.select(TARGET.beta, {
  ssh: (p, beta) => p.stage(steps.helmDeployOverSsh({ name: "deploy-beta", description: "", target: beta, sshKey: SSH_PRIVATE_KEY, chart: "helm" })),
  inCluster: (p, beta) => p.stage(steps.helmDeployInCluster({ name: "deploy-beta", description: "", target: beta, chart: "helm" })),
})
```

- `PUBLISH.repository` is a typed reference: it must have the field's type (a `number` field or an
  optional one can't go where a `string` is required). Union fields that every variant has are
  reachable (`AUTH.mode`). A field named `kind`, `name`, `description` or `schema` is reached through
  `$` (`PUBLISH.$.name`), since those are the entry's own properties.
- The step receives the referenced entry automatically (its `configEntries`), and reads the value from
  the run's config, defaults applied, when it runs. The manifest records
  `{"$config": "publish.repository"}`; reconcile checks the entry is one the step receives and the
  field exists.
- An entry that contains a `secretRef` must have its secrets declared by every step that receives it
  (`imageBuild` takes `secrets: [...]` for that). A library step that references parts of an entry
  needs only the secrets those parts name, narrowed to its `select` case: a `helmDeployOverSsh` in
  the `ssh` case of a `deployTarget()` doesn't declare the compose case's `token`.

## The config document

One YAML document per pipeline, with one top-level key per entry:

```yaml
# app.yml
target:
  host: oliver@bulwark-m2          # namespace takes its default
auth:
  mode: dex
  issuer: https://dex.example.com
  clientId: olve-pipelines
  clientSecret: { secret: DEX_CLIENT_SECRET }
```

- **Every save is a full, immutable snapshot**: `config #N`, with author and time. Nothing is ever
  edited in place. Snapshots are kept for the lifetime of the pipeline.
- **Rollback** saves an old snapshot's content again as the newest (`#N+1`).
- **Saves are validated against the active version.** A document the active version rejects is not
  saved, so a typo can't break what is running. Keys the active version doesn't declare are
  ignored, so you can save the config a future version needs *before* pushing that version.
- The YAML is stored exactly as written, comments included. YAML 1.2 core types: `no` is a
  string, `true`/`false` are booleans; anchors and aliases are not supported.
- Diffs between snapshots are computed for display only.

The web UI's **Config** page (from a pipeline's page) has a YAML editor that marks errors on the
offending lines and completes keys and values from the version's schema, the snapshot history with
rollback, and a diff between any two snapshots.

## Config and secrets

| | Secret | Config |
|---|---|---|
| Typing | opaque string | zod schema, validated |
| Read back | never (only *is set*) | yes: `pl config get`, the UI |
| In logs and outputs | masked | shown |
| History | none | full snapshots |
| Resolved | per **attempt**, from the pipeline's Kubernetes Secret (a rotated value applies on the next attempt) | per **run** (the snapshot pinned at run start) |

Config is readable by anyone who can read the pipeline: anything sensitive must be a secret,
referenced by name with `secretRef`. Saving config and rolling it back need the same authorization
as `pl secret set`.

## Versions: pending until satisfied

When reconcile produces a new version, it is checked against the current config document and secrets:

- **Satisfied**: every required entry is present and valid, and every secret the config
  *references* (`secretRef`) is set. Other declared secrets stay optional (an unset secret is an
  unset env var). The version is switched to as usual.
- **Not satisfied**: the version is stored as **pending** and the active version stays active.
  `pl binding status` and `pl pipeline health` say why:

  ```text
  Pending:     pending v9: config config.auth.clientId: is required; secret DEX_CLIENT_SECRET not set
  ```

  Saving config or setting the secret re-checks, and the version activates on its own once
  satisfied.

- While a version is pending, automatic triggers (push, poll, webhook) are held, like any unapplied
  config commit: the push that produced the version starts its run once the version activates.
  Manual triggers start on the active version.
- `pl pipeline switch` goes through the same check and is refused with the reasons when the target
  isn't satisfied.
- Deleting a secret that the current config references needs `pl secret delete … --force`. The
  active version is then **held** (`held v8: secret … not set`): new runs are refused until the
  secret is set again. Runs already going carry on; their next attempt starts without the secret.

## Runs pin their config

A run pins the latest config snapshot when it starts, like it pins the version, and records its
**effective config**: the snapshot with the pinned version's defaults applied. Later saves don't
affect it.

```sh
pl run get my-app#42
# version:   v8, config #5 · 2026.10.24.42 · commit 1a2b3c4
# config:    {"auth":{"mode":"none"},"target":{"host":"h","namespace":"olve-pipelines"}}
```

- `pl run continue` and `pl step rerun` keep the run's snapshot; there is no way to switch a run to
  newer config. To pick up new config, start a new run (`pl trigger` or `pl run rerun`, which makes a
  new run with the current snapshot).
- Each attempt gets its step's entries in `input.json` under `$config`; the harness parses them with
  the entry schemas and hands them to the handler as `ctx.config`.

## Defaults

Defaults live in the schema (`.default(…)`), so they belong to a version. The saved document
stays as written: defaults are applied when config is read (validation, run start), never written
into the YAML, so a later default change still reaches instances that never set the field.

A changed default is shown before it applies: `pl config check --version V` and the pending view
list every default that differs from the active version's *and* that the current document relies
on, e.g. `v9 changes the default of telemetry.exporter ("none" → "otlp")`. It doesn't block
activation.

Give a field a default when one value is right for most people and safe; require it when no value
can work for everyone (a host, your domain). A default must never expose an instance.

## Commands

```sh
pl config get <pipeline> [--snapshot N] > app.yml   # current (or an old) snapshot, as written
pl config set <pipeline> -f app.yml                 # validate and save as a new snapshot (or pipe on stdin)
pl config history <pipeline>                        # snapshots: number, author, time
pl config rollback <pipeline> <N>                   # save snapshot N again as the newest
pl config check <pipeline> [--version V]            # the current document against V (default: pending, else active)
```

`pl config check` prints the problems, the referenced secrets that aren't set, the changed defaults
and the effective config, and exits 1 when the version couldn't activate with this config.

## See also

- [Pipeline Reference](pipeline-reference.md) — steps, secrets and the checks that reject a pipeline
- [Binding, Reconcile & Versions](binding-and-reconcile.md) — how a commit becomes a version
- [Runs & Operations](runs-and-operations.md) — continue, rerun and how a step runs
