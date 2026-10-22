---
name: setup-pipeline
description: Recreate the v2 self-deploy pipeline ("Olve.Pipelines v2") on the v2 beta instance by binding it to the repo's v2 branch (GitOps); reconcile builds .pipelines/pipeline.ts into a version.
allowed-tools: Bash Read
---

# Set up the v2 self-deploy pipeline (GitOps)

The **bootstrap runbook** for v2 beta (`https://pipelines-v2-beta.ovea.pro`): it creates the
pipeline bound to `OliverVea/Olve.Pipelines@v2`, path `.pipelines`, and sets its secrets.
Reconcile then compiles [`.pipelines/pipeline.ts`](../../../.pipelines/pipeline.ts) into a version
(`source` → `build` ∥ `test` → `deploy-beta`). How it works: `docs/setup/getting-started.md` and
`docs/setup/binding-and-reconcile.md`.

Only needed after a hard reset (v2 Garage state deleted). To change *what* the pipeline does, edit
`.pipelines/pipeline.ts` (+ `handlers/`) and push to `v2` — there are no shape-mutation commands.

**Never have two deployers active.** The v1 pipeline "Olve.Pipelines v2" on v1 prod
(`pipelines-private.ovea.pro`, bound to the `v2` branch, running `.pipelines/config.yaml`) is paused
by its blocked `deploy-beta` promotion gate. Leave it blocked. (Re-enabling it is the rollback in
the E2 notes of `docs/design/pipelines-v2-plan.md` — and then the v2 self-deploy must be paused with
`pl gate block "Olve.Pipelines v2/deploy-beta"` first.)

## Step 0: v2 `pl` and session

```bash
dotnet publish src/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/opt/pl-v2   # v2 pl (install.sh serves v1's)
export PATH=~/.local/opt/pl-v2:$PATH PIPELINES_CONFIG=~/.pl-v2 PIPELINES_API_URL=https://pipelines-v2-beta.ovea.pro
pl login                                    # --device over SSH; depends on the Authentik worker being healthy
```

Keep these variables exported in every shell invocation below.

## Step 1: Verify the app is running

```bash
curl -s https://pipelines-v2-beta.ovea.pro/api/health
curl -s https://pipelines-v2-beta.ovea.pro/api/ready
```

If unreachable: `ssh oliver@bulwark-m2 "kubectl -n apps-beta rollout restart deploy/olve-pipelines-v2 && kubectl -n apps-beta rollout status deploy/olve-pipelines-v2 --timeout=120s"`.

## Step 2: Confirm no pipeline exists

```bash
pl pipeline list
```

If "Olve.Pipelines v2" still exists, the state you meant to reset is still there — **stop**:
`pl binding create` mints a new id every time and would create a second self-deployer.

## Step 3: Validate the pipeline against this instance

```bash
(cd sdk && bun install) && (cd .pipelines && bun install)
pl dev validate --dir .pipelines            # describe locally + the service's reconcile checks
```

Expect `valid` (one `output-unused` warning on deploy-beta is fine).

## Step 4: Create the pipeline bound to the repo

The repo is public, so no credentials key is needed. v2 beta has no public webhook route, so the
trigger must be `poll`.

```bash
PID=$(pl binding create OliverVea/Olve.Pipelines --branch v2 --path .pipelines --trigger poll --json | jq -r .pipelineId)
echo "Pipeline: $PID"
```

## Step 5: Set the secrets

`pipeline.ts` declares `GITHUB_TOKEN` (repo tarball for the `source` step) and `SSH_PRIVATE_KEY`
(image import + helm on the host). Values live in `olve-pipeline-<id without dashes>` in
`olve-runners-beta`. Copy them from the previous pipeline's Secret — the old v2 one in
`olve-runners-beta`, or v1's self-deploy Secret in `olve-runners`:

```bash
ssh oliver@bulwark-m2 "kubectl get secrets -n olve-runners-beta | grep olve-pipeline-"
OLD_NS=olve-runners-beta OLD_SECRET="<name from above>"
for KEY in GITHUB_TOKEN SSH_PRIVATE_KEY; do
  ssh oliver@bulwark-m2 "kubectl get secret $OLD_SECRET -n $OLD_NS -o jsonpath=\"{.data.$KEY}\"" \
    | base64 -d | pl secret set "$PID" "$KEY"
done
```

Pipe the bytes straight in — never through `$(…)`, which strips the SSH key's trailing newline.
If there is no previous Secret, set `GITHUB_TOKEN` to a GitHub read token and `SSH_PRIVATE_KEY` to
the homelab deploy key out-of-band, the same way (stdin).

## Step 6: Reconcile

```bash
pl binding reconcile "$PID"                 # returns at once; the build Job takes a minute or two
pl binding status "$PID"                    # repeat until Reconcile: Success
pl pipeline versions "$PID"                 # version 1, linked to the latest .pipelines/ commit
pl pipeline manifest "$PID"                 # source → build, test → when deployTo=beta: deploy-beta
```

`Error` → read the problems (`pl dev validate` reproduces manifest problems). The first reconcile
only records the branch head; it does not deploy.

## Step 7: Verify a deploy

Run `/deploy` (manual trigger with the current `v2` head), or push to `v2` and wait for the deploy
poll (up to 15 min). Optional: rebind the fixture the same way
(`pl binding create OliverVea/Olve.Pipelines --branch v2 --path .pipelines-test --trigger poll`, no
secrets).

## After setup

Tell the user: the pipeline id, the binding (repo@branch, path, trigger), the reconcile result and
the secrets' state; that pushes to `v2` now deploy v2 beta automatically; that pipeline changes go
through `.pipelines/pipeline.ts`; and that `/deploy` starts a run by hand.
