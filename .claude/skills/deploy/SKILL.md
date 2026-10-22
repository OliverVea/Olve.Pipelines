---
name: deploy
description: Trigger the v2 self-deploy run of Olve.Pipelines (on v2 prod, from the v2 branch's .pipelines/pipeline.ts), monitor it, and hand the promotion to prod to the user.
allowed-tools: Bash Read
---

# Deploy Olve.Pipelines v2

The pipeline **"Olve.Pipelines v2"** lives on **v2 prod** (`https://pipelines-v2-private.ovea.pro`,
Helm release `olve-pipelines-v2` in `apps`, runners in `olve-runners`), bound to
`OliverVea/Olve.Pipelines@v2`, path `.pipelines`. Stages: `source` ∥ `mirror-base-images` →
`build` ∥ `test` → `deploy-beta` → `beta-test` → when `deployTo: "prod"`: `deploy-prod` →
`publish-cli` ∥ `publish-sdk`.

- Every push to `v2` starts a run with `deployTo: "beta"` (deploy poll, up to 15 min): it deploys
  **v2 beta** (`https://pipelines-v2-beta.ovea.pro`, release `olve-pipelines-v2` in `apps-beta`) only.
- **Promotion** to v2 prod is `pl run continue … --field deployTo=prod` on a green beta run. Claude
  doesn't start prod deploys: the user runs (or approves) the promotion.

(v1 prod/beta are deployed by v1 from `main`'s `.pipelines/config.yaml`; the v1 pipeline bound to
the `v2` branch is paused by its blocked `deploy-beta` gate and must stay paused, or two deployers
race.)

## Prerequisites

- A v2 `pl`: `curl -fsSL https://pipelines-v2-private.ovea.pro/install.sh | PL_INSTALL_DIR=~/.local/opt/pl-v2 sh`,
  or from source: `dotnet publish src/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/opt/pl-v2`.
- A session against v2 prod. `~/.pl` holds one session, so keep v2's separate:
  ```bash
  export PATH=~/.local/opt/pl-v2:$PATH PIPELINES_CONFIG=~/.pl-v2 PIPELINES_API_URL=https://pipelines-v2-private.ovea.pro
  pl login            # --device over SSH
  ```
  Every command below assumes these variables are exported in the same shell invocation.
- If the pipeline doesn't exist (`pl pipeline list` shows no "Olve.Pipelines v2"), run `/setup-pipeline` first.

## 1. Check the pipeline

```bash
P="Olve.Pipelines v2"
pl pipeline health "$P"        # last run, running/held runs, blocked gates, active version
pl gate list "$P"              # deploy-beta must NOT be listed, or the run will be held there
pl run list "$P" --page-size 5
```

If `deploy-beta` is blocked, someone paused the self-deploy on purpose — ask before
`pl gate unblock "$P/deploy-beta"`. A blocked `deploy-prod` is normal between promotions.

## 2. Trigger (beta)

The input model requires a full 40-hex `sha`. `deployTo` defaults to `prod`, so pass `beta`
explicitly:

```bash
git fetch origin v2
pl trigger "$P" --field sha=$(git rev-parse origin/v2) --field deployTo=beta   # prints "Olve.Pipelines v2#<n>"
```

An explicit trigger is never de-duplicated; it does not supersede a running automatic run (both
run; the per-step slot serialises them).

## 3. Monitor

```bash
R="Olve.Pipelines v2#<n>"
pl run get "$R"                                  # stages + each step's status, attempt, reason
pl step logs "$R/build" --follow                 # kaniko build
pl step logs "$R/test" --follow                  # unit tests
pl step logs "$R/deploy-beta" --follow
pl step logs "$R/beta-test" --follow             # smoke test of the new beta
```

Expected: `source` ~5 s; `build` ~1.5 min cached, up to ~4.5 min cold; `test` ~1 min (parallel with
build); `deploy-beta` ~20 s; `beta-test` a few seconds. If `beta-test` fails, the run fails and
can't be promoted.

## 4. Promote to prod (the user)

On a succeeded beta run, tell the user the command; they run or approve it:

```bash
pl gate list "$P"                                       # if deploy-prod is blocked: pl gate unblock "$P/deploy-prod"
pl run continue "$R" --field deployTo=prod             # deploy-prod → publish-cli ∥ publish-sdk
pl step logs "$R/deploy-prod" --follow
```

`deploy-prod` replaces the pod that runs the service: the new pod logs `Reattaching to K8s Job …
deploy-prod`, collects it and finishes the run — a short API blip during that is normal.
`publish-cli` then publishes the `pl` that prod serves at `/download`; `publish-sdk` runs `npm
publish` if `sdk/`'s version is new. The user may block `deploy-prod` again afterwards
(`pl gate block "$P/deploy-prod"`).

## 5. On failure

- `pl run get "$R"` shows the failed step's reason (includes the harness exit code); `pl step logs "$R/<step>"`.
- **kaniko hangs at "Unpacking rootfs"** (a stalled base-image pull): `steps.imageBuild`'s stall
  watchdog fails the attempt after 300 s of silence, and the build's `timeoutSeconds: 900` bounds it
  anyway. Rerun it: `pl step rerun "$R/build" --continue`, or the whole run:
  ```bash
  pl run cancel "$R" && pl run rerun "$R"
  ```
- Rerun just the failed step and continue downstream: `pl step rerun "$R/<step>" --continue`.
- Service logs: `ssh oliver@bulwark-m2 "kubectl -n apps logs deploy/olve-pipelines-v2 --since=10m | grep -v health"`.
- Step pods (`olve-runners` is shared with v1 prod, so filter by label):
  `ssh oliver@bulwark-m2 "kubectl -n olve-runners get jobs,pods -l olve-pipeline=6a395b723bb342a7b01ae28a6f138032,olve-run=<n>"`
  (labels `olve-pipeline`, `olve-run`, `olve-step`, `olve-attempt`, `olve-role=step`; containers `olve-init`, `step`).

## 6. Verify

```bash
curl -s https://pipelines-v2-beta.ovea.pro/api/health
ssh oliver@bulwark-m2 "kubectl -n apps-beta get deploy olve-pipelines-v2 -o jsonpath='{.spec.template.spec.containers[0].image}'"
# after a promotion, the same against prod:
curl -s https://pipelines-v2-private.ovea.pro/api/health
ssh oliver@bulwark-m2 "kubectl -n apps get deploy olve-pipelines-v2 -o jsonpath='{.spec.template.spec.containers[0].image}'"
# expect olve-pipelines-v2:<the run's version stamp, e.g. 2026.10.22.6>
```

Report the run address, its duration, the deployed image tag(s) and the health result.
