# Olve.Pipelines v2 — typed pipeline config

Status: **design agreed, not implemented** (2026-10-24). Extends [pipelines-v2.md](pipelines-v2.md);
section references like §6.2 point there.

## 1. Why

A pipeline repo describes *how* something is built and deployed. It should not hold the values that
differ per deployment: hosts, namespaces, domains, which identity provider to use. Today those
values are hard-coded in `.pipelines/pipeline.ts` (e.g. `host: "oliver@bulwark-m2"`), so anyone else
who wants to run, say, Olve.AgentRuntimeManager on their own instance has to fork the repo.

**Goals**

1. A pipeline declares a **typed config schema**. Alternatives are unions (`auth` is `none`, `dex`
   or `authentik`, each with its own required fields).
2. **Values are stored on the instance**: one YAML document per pipeline, edited in the UI or with
   `pl`. No config repo.
3. Config and **secrets are different kinds** with different rules (§3).
4. **Nothing that already works breaks.** Runs keep the config they started with; a new version
   that needs config the instance doesn't have yet is not activated (§7).

**Non-goals (for now):** a config repo or git-reconciled config, diff-based history, a generated
form editor, an escape hatch for runs held behind a pending version, per-environment overlays.

## 2. Concepts

| Concept | What it is |
|---|---|
| **Config entry** | A named, typed value the pipeline declares: `config("auth", description, schema)`. |
| **Config schema** | The object of all entries a version declares, in the version's manifest. |
| **Config document** | The instance's YAML for one pipeline: one key per entry. |
| **Config snapshot** | One saved, immutable, complete config document: `config #N`. |
| **Secret** | Unchanged from §3.3: a named, write-only string, declared with `secret(...)`. |
| **Secret reference** | A config field that *names* a secret: `{ secret: DEX_CLIENT_SECRET }`. |

## 3. Config vs secrets

They are separate kinds, not one store with a "sensitive" flag.

| | Secret | Config |
|---|---|---|
| Typing | opaque string | zod schema, validated |
| Read back | never: the API, `pl` and the UI only show *is set* | yes: `pl config get`, the UI |
| In logs and outputs | masked | shown |
| History | none exposed (rotation) | full snapshots (§6) |
| Resolved | per **attempt**, from the pipeline's K8s Secret via `secretKeyRef` (a rotated token applies on the next attempt) | per **run** (snapshot pinned at run start), delivered in the attempt's `input.json` |
| References | — | may reference secrets by name, never hold their value |

**Environment variables are a delivery mechanism, not a third kind.** A step receives its config as
typed input (`ctx.config`) and its secrets as today (`ctx.secrets`, same-named env vars). A step
or library step decides how to pass either on to the tool it runs: env vars, files, `--set` flags.

## 4. Authoring

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
      allow: z.object({ groups: z.array(z.string()).optional(), emails: z.array(z.string()).optional() }),
    }),
    z.object({ mode: z.literal("authentik"), issuer: z.string().url(), audience: z.string() }),
  ]));

export const TARGET = config("target", "Where the release is deployed.",
  z.object({ host: z.string(), namespace: z.string(), release: z.string() }));

export const deploy = step({
  name: "deploy",
  description: "Deploys the release.",
  image: "alpine/helm:3",
  config: [AUTH, TARGET],             // like `secrets: [...]`: only declared entries are delivered
  secrets: [DEX_CLIENT_SECRET],       // a secretRef's secret must also be declared by the step
  // ...
});
```

- `config(name, description, schema)` mirrors `secret(...)`. Names match `^[a-z][a-zA-Z0-9]*$`
  (they are YAML keys, not env var names).
- Steps declare the entries they use; `ctx.config` is typed to exactly that set. Library steps
  accept config values in their typed definitions (e.g. `host: TARGET.host`).
- `secretRef(S)` is a schema whose value in YAML is `{ secret: S.name }`. Build-time checks:
  - a step that receives a config entry containing a `secretRef` must also declare that secret
  - a `secretRef` can only name a declared secret
- Defaults are allowed (`.default(...)`). An entry whose schema accepts `undefined` is optional;
  every other entry is required. When to give a default: §8.

## 5. Manifest

A version's manifest gains:

```json
"config": [
  { "name": "auth", "description": "…", "schema": { /* JSON Schema */ } },
  { "name": "target", "description": "…", "schema": { … } }
],
"steps": [ { "name": "deploy", "config": ["auth", "target"], "secrets": ["DEX_CLIENT_SECRET"], … } ]
```

A changed config schema is a manifest change: it makes a new pipeline version (§6.1), and the
steps that declare that entry get new step versions.

## 6. Storage and editing

- **One document per pipeline.** It holds every entry any version might need. Validation is
  against a version's schema and ignores keys that version doesn't declare, so you can add the
  config a future version needs *before* pushing that version.
- **Every save is a full immutable snapshot:** `config #N` with author and time. Snapshots are
  never edited or deleted while a retained run references them.
- **Rollback** saves an old snapshot again as the newest one (`#N+1`, same content as `#K`).
- **Diffs** are computed between two snapshots for display only; nothing stores a diff.
- **Saving is validated** against the active version's schema. A document the active version
  rejects can't be saved, so the running setup can't be broken by a typo. The result for a pending
  version (§7) is shown alongside, without blocking the save.

CLI:

```sh
pl config get <pipeline> [--snapshot N] > app.yml   # current (or an old) snapshot as YAML
pl config set <pipeline> -f app.yml                 # validate and save as a new snapshot
pl config history <pipeline>                        # snapshots: number, author, time
pl config rollback <pipeline> <N>                   # save snapshot N again as the newest
pl config check <pipeline> [--version V]            # validate the current document against a version
```

UI: per pipeline, a **Config** tab with a YAML editor validated against the schema in the browser
(errors on the offending lines, completion from the JSON Schema), the snapshot history, and a diff
between any two snapshots.

## 7. Versions, activation and runs

**Runs pin a config snapshot as they pin a version** (§5.2). `pl run get` shows
`v8, config #5`. Later saves don't affect a run that has started. The run also records its
**effective config**: the snapshot with the pinned version's defaults applied (§8), so a run stays
reproducible even if a later version changes a default.

| Situation | Runs already going | New runs |
|---|---|---|
| Config saved on the active version | keep their snapshot | use the new snapshot |
| Invalid config | can't happen: rejected on save | — |
| New version whose config isn't satisfied | carry on (pinned version and snapshot) | held until it activates |
| Secret rotated | next attempt gets the new value | new value |
| Secret referenced by config unset or deleted | next attempt starts without it (env var unset, as today) | held, like missing config |

**Activation gate.** When reconcile produces a new version (§6.2), it is checked against the
current config document and secrets:

- **Satisfied:** every required entry is present and valid, and every secret the config
  *references* (`secretRef`) is set. Other declared secrets stay optional, as today (an unset secret
  is an unset env var), so existing pipelines aren't blocked by secrets they never set. The version is switched to, as today.
- **Not satisfied:** the version is stored as **pending** and the active version stays active.
  `pl binding status` and `pl pipeline health` say why, e.g. `pending v9: config auth.mode=dex
  requires clientId; secret DEX_CLIENT_SECRET not set`. Saving config or setting the secret
  re-checks, and the version activates automatically once satisfied.
- `pl pipeline switch` goes through the same check.
- Under config-before-run, a push whose commit produced the pending version is held until it
  activates. An escape hatch for held runs is out of scope for now.

**Continue and rerun reuse the run's snapshot.** `pl run continue` and `pl step rerun` always use
the config snapshot the run pinned, like its version. There is deliberately **no**
`--current-config`: to pick up new config, start a new run (`pl trigger` / `pl run rerun`).
`pl run rerun` makes a new run, so it gets the current snapshot.

Deleting a secret that the current config references asks for confirmation (`--force`).

## 8. Defaults

**Principle: a fresh self-hosted install works with the defaults.** Give a field a default when one
value is right for most people *and* safe. Require it only when no value can work for everyone:
which cluster or host, your domain. "Secure by default" outranks "just works": a default must never
expose an instance (so `auth` does not default to `none`).

- **Defaults live in the schema** (`.default(...)`), so they are part of the version and its
  manifest, and the UI and `pl config check` can show them.
- **The saved document stays as written.** Defaults are applied when config is read (validation,
  run start), never written into the YAML. A later default change therefore still reaches instances
  that never set the field.
- **Changed defaults are surfaced.** `pl config check --version V` and the pending / activation
  view list every default that differs from the active version's *and* that the current document
  relies on, e.g. `v9 changes the default of telemetry.exporter (none → otlp)`. A changed default
  doesn't block activation; it is shown so the switch is never a surprise.
- **Runs record their effective config** (§7), not only the snapshot number.

For Olve.Pipelines deploying itself (§9):

| Entry | Default | Why |
|---|---|---|
| `target.cluster` / host | **required** | no sensible default |
| `target.namespace` | `olve-pipelines` | a convention works |
| `publicUrl` (links, webhooks) | none: no links or webhooks until set | depends on your DNS |
| `storage` | the bundled Garage | nothing external needed |
| `auth` | bundled Dex with a generated initial admin, stored as a secret and printed once by `pl bootstrap` | secure and works; `none` would leave an exposed instance open |
| `telemetry` | `exporter: none` | a collector is something you set up |
| harness image | the ghcr image this release published, pinned by digest | public and version-matched |
| registry caches | none: images are pulled directly | an optimisation, not a requirement |

## 9. Applying it to Olve.Pipelines itself

Olve.Pipelines' own pipeline declares its deployment config instead of hard-coding it:

- `target`: hosts, namespaces and releases for beta and prod (today `oliver@bulwark-m2`,
  `apps-beta`, `apps`).
- `auth`, as in §4. **Prod moves from Authentik to Dex.**
  - `mode: none` is a supported variant (local, throwaway or trusted-network setups). It replaces
    `Auth__Disable` (and makes `Auth:AnonymousReads` a field of the variants that have users).
  - In `dex` mode the server needs an **authorization rule** (`allow.groups` / `allow.emails`).
    Today any valid token is accepted, which is only safe while the identity provider itself
    limits who can sign in. Dex with an upstream connector (GitHub, Google) does not.
  - `pl login --device` needs the provider to advertise a device-authorization endpoint. Enable
    Dex's device flow.
- `telemetry`:

  ```yaml
  telemetry:
    exporter: none            # or otlp:
    # exporter: otlp
    # endpoint: https://otel.ovea.pro
    # auth: { type: none }
    #     | { type: oauth2, tokenUrl: …, clientId: otel, clientSecret: { secret: OTEL_CLIENT_SECRET } }
    #     | { type: header, name: Authorization, value: { secret: OTEL_TOKEN } }
  ```

  Today the exporter gets an OAuth2 client-credentials token from Authentik
  (`OpenTelemetry:OAuth2:TokenUrl`). On Dex, check whether the deployed Dex version supports the
  client-credentials grant. If it does, use `oauth2` against Dex. If not, use a static `header`
  token, so Authentik isn't kept running only for telemetry.

The deploy steps render these entries to helm values, which the chart turns into the server's
`Auth__*` / `OpenTelemetry__*` settings. Local compose and `pl bootstrap` set the same settings
directly, with `auth: none` or the bundled Dex and `telemetry: none`.

## 10. Security

- Config is readable by anyone who can read the pipeline. Anything sensitive must be a secret,
  referenced by name.
- Secret values never enter a config snapshot, the manifest, logs or run records. Masking in logs
  follows pipelines-v2 §8.
- Saving config and rolling it back are writes; they need the same authorization as
  `pl secret set`.

## 11. Implementation order

1. SDK: `config()`, `secretRef()`, step `config: [...]`, typed `ctx.config`, build checks,
   manifest emission. Tests.
2. Server: config document storage with snapshots; validation against a version's JSON Schema;
   the activation gate in reconcile and switch; runs pin `configSnapshot`; deliver `ctx.config`
   to attempts.
3. CLI: `pl config get|set|history|rollback|check`; show the snapshot in `pl run get`; show the
   pending reason in `pl binding status` / `pl pipeline health`.
4. UI: Config tab (YAML editor with schema validation, history, snapshot diff).
5. Olve.Pipelines' own pipeline (§9): declare `target`, `auth` and `telemetry`; add `auth.mode`
   and the authorization rule to the server; make telemetry auth configurable; deploy Dex; switch
   prod.

## 12. Open questions

- Which Dex version, and does it support client credentials (decides `telemetry.auth` in prod)?
- Masking approach for config-referenced secrets in logs (shares pipelines-v2 §8's open item).
- Whether a trigger mapper (`onPush`) may read config. Out of scope for the first cut.
