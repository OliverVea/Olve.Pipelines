# Example Pipelines

[← Index](index.md) · [Subject Index](subjects.md)

Four real projects, ordered by how hard they push the model. Each is a concrete
`.pipelines/config.yaml`, mapped to the capability it exercises and the **shared block** it
would let you stop copy-pasting. Read top-to-bottom: the model handles #1 and #2 cleanly today,
#4 needs no new capability (just plumbing), and #3 is the one that stresses the design.

> **The thread running through all four:** the same build/deploy *blocks* are copy-pasted
> across projects. Olve.Pipelines already composes **within** a repo (`$ref` pulls a step from
> its own file, `scriptFile` pulls a script — see
> [config-reference.md](config-reference.md#file-extraction-ref-and-scriptfile)), but only
> within that repo's config subtree. Every example below re-types the same Kaniko build or
> SSH/Helm deploy. That repetition — not a missing feature — is the signal for what's next.

---

## 1. Olve.Pipelines (this repo) — build → beta → prod

**Status: onboarded.** The canonical shape: one parallel build, two sequential deploys where
list order makes beta gate prod.

```yaml
apiVersion: "0.0"
name: olve-pipelines
secrets:
  - name: GITHUB_TOKEN
  - name: SSH_PRIVATE_KEY
productionSteps:
  - name: build-and-package          # Kaniko build → image.tar + helm chart + version into the bundle
    configuration:
      image: gcr.io/kaniko-project/executor:debug
      scriptFile: scripts/build.sh
processingSteps:
  - name: deploy-beta                 # SSH to host, import image, helm upgrade beta
    configuration: { image: alpine:latest, scriptFile: scripts/deploy-beta.sh }
  - name: deploy                      # same, prod — runs ONLY if deploy-beta succeeded
    configuration: { image: alpine:latest, scriptFile: scripts/deploy.sh }
```

- **Exercises:** parallel production (of one), sequential processing as a gate.
- **Copy-paste block:** `scripts/build.sh` is ~40 lines of Kaniko + busybox-wget + tarball
  fetch; `deploy-*.sh` are ~40 lines of SSH + nerdctl import + helm upgrade.

## 2. OliverVea/QuestionBank — same skeleton, different app

**Status: onboarded.** Structurally identical to #1 with a different image/chart. **This is the
point:** the second project proved the model by repeating the first — including repeating the
build and deploy scripts almost verbatim. Two repos, two copies of the same Kaniko block.

- **Exercises:** nothing new — it demonstrates the *repetition*.
- **Copy-paste block:** the *same* Kaniko build and SSH/Helm deploy as #1, re-vendored.

## 3. Homelab config — authentik, openbao, + N self-hosted apps

**Status: not onboarded — and the one that stresses the model.** A homelab isn't one app; it's
~a dozen *independent* deployments (authentik, openbao, several self-written apps). Two things
break the single-pipeline assumption:

1. **Independent outputs don't combine.** The ArtifactBundle model assumes parallel production
   steps *combine* into one bundle that processing consumes
   ([bundles-and-execution.md](bundles-and-execution.md)). Independent apps don't combine — each
   has its own image and its own deploy. Modeling them as production steps of one pipeline
   would zip unrelated images into one bundle and deploy them together.
2. **So this is N pipelines, not one.** Each app is its own bind. Which surfaces the real gap:
   those N pipelines are near-identical — same build block, same deploy block, differing only
   in name/chart/namespace.

```yaml
# One representative app — repeated, with variations, ~12 times across the homelab.
apiVersion: "0.0"
name: authentik
secrets: [ { name: GITHUB_TOKEN }, { name: SSH_PRIVATE_KEY } ]
productionSteps:
  - name: build
    configuration: { image: gcr.io/kaniko-project/executor:debug, scriptFile: scripts/build.sh }
processingSteps:
  - name: deploy
    configuration: { image: alpine:latest, scriptFile: scripts/deploy.sh }   # namespace/chart differ per app
```

- **Exercises:** the limit of *repo-local* composition. The bundle composes outputs *within* a
  pipeline; nothing composes the *pipeline pattern* across a dozen apps.
- **Copy-paste block:** the entire pipeline shape, ~12×. This is where composability has to
  move from "share a step within a repo" to "share a pattern across pipelines."

## 4. Olve.Trains (game) — multi-platform build, gated publish

**Status: not onboarded — but expressible today.** Build for Windows + Linux in parallel, run
visual-regression tests, then publish to itch.io and Steam — publishes gated behind tests, and
itch before Steam, via list order.

```yaml
apiVersion: "0.0"
name: olve-trains
secrets:
  - { name: GITHUB_TOKEN }
  - { name: ITCH_API_KEY }       # butler push
  - { name: STEAM_CONFIG }       # steamcmd build account
productionSteps:                 # PARALLEL — two platform builds into one bundle
  - name: build-windows
    configuration: { image: <godot-export-image>, scriptFile: scripts/export-windows.sh }
  - name: build-linux
    configuration: { image: <godot-export-image>, scriptFile: scripts/export-linux.sh }
processingSteps:                 # SEQUENTIAL — order is the gate chain
  - name: test                   # visual-regression over both builds; failure stops publishing
    configuration: { image: <test-image>, scriptFile: scripts/visual-regression.sh }
  - name: publish-itch           # butler push to itch.io
    configuration: { image: <butler-image>, scriptFile: scripts/publish-itch.sh }
  - name: publish-steam          # steamcmd to Steam — runs only if itch published
    configuration: { image: <steamcmd-image>, scriptFile: scripts/publish-steam.sh }
```

- **Exercises:** real production parallelism (two platforms → one bundle), a longer sequential
  gate chain (test → itch → steam), and secrets per external target.
- **No missing capability.** The only thing the model lacks is *matrix fan-out* (one step ×
  platforms), but at two platforms you just write two steps; it only bites at scale.
- **Copy-paste block:** the Godot export script (Windows/Linux differ by one flag), and the
  publish scripts — the same pattern other game projects would re-vendor.

---

## What the four examples say about the next step

Read together, they don't ask for a *feature* — they ask for **composition that crosses the
repo boundary**:

- **#1 → #2** copy the same Kaniko build block between two repos.
- **#4** would copy the export/publish blocks to the next game.
- **#3** copies the *entire pipeline shape* a dozen times.

The system already has the composition primitive — `$ref`/`scriptFile` — but it's confined to a
single repo's config subtree. Lifting it to a **shared, referenceable block** (a `KanikoBuild`,
a `HelmDeploy`, a `GodotExport`) is the natural next step. That choice has one real fork, and it
trades against the **"your repo is the single source of truth / self-contained"** principle:

| Approach | Self-contained? | Duplication | Versioning |
|---|---|---|---|
| **Vendored** — a generator copies blocks into each repo | ✅ preserved | git dup remains; authoring dup gone | block updates re-run the generator |
| **Referenced** — repo points at an external block library | ❌ repo depends on the library | DRY | needs pinning/version semantics |

That tradeoff is the design decision behind "composable, flexible, non-complex, safe" — it isn't
settled here, it's the thing to decide next.
