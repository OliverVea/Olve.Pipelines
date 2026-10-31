---
name: setup-pipeline
description: Recreate the v2 self-deploy pipeline ("Olve.Pipelines v2") on the v2 prod instance by binding it to the repo's main branch (GitOps); reconcile builds .pipelines/pipeline.ts into a version.
allowed-tools: Bash Read
---

# Set up the v2 self-deploy pipeline (GitOps)

The **bootstrap runbook** for v2 prod (`https://pipelines-private.ovea.pro`, release
`olve-pipelines-v2` in `apps`, runners `olve-runners`, its bundled Dex): it creates the pipeline
bound to `OliverVea/Olve.Pipelines@main`, path `.pipelines`, and sets its secrets. Reconcile then
compiles [`.pipelines/pipeline.ts`](../../../.pipelines/pipeline.ts) into a version (`source` →
`build` ∥ `test` → `deploy-beta` → `beta-test` → when `deployTo: "prod"`:
`deploy-prod` → `publish-cli` ∥ `publish-sdk`). How it works: `docs/setup/getting-started.md` and
`docs/setup/binding-and-reconcile.md`.

Only needed after a hard reset (v2 prod Garage state deleted). To change *what* the pipeline does,
edit `.pipelines/` and push to `main` — there are no shape-mutation commands.

**Never have two deployers active.** Exactly one "Olve.Pipelines v2" may run this pipeline, on
v2 prod. Don't create it on v2 beta.

## Step 0: v2 `pl` and session

```bash
(cd src/spec && bun install && bun run build) && (cd src/cli && bun install && bun run build) && cp artifacts/cli/pl ~/.local/bin/   # a reset prod serves no pl yet
pl login --device --api-url https://pipelines-private.ovea.pro   # Dex sends you to GitHub
```

Keep these variables exported in every shell invocation below.

## Step 1: Verify the app is running

```bash
curl -s https://pipelines-private.ovea.pro/api/health
curl -s https://pipelines-private.ovea.pro/api/ready
```

If unreachable: `ssh oliver@bulwark-m2 "kubectl -n apps rollout restart deploy/olve-pipelines-v2 && kubectl -n apps rollout status deploy/olve-pipelines-v2 --timeout=120s"`.

## Step 2: Confirm no deployer exists

```bash
pl pipeline list
curl -s https://pipelines-beta.ovea.pro/api/pipelines | jq -r '.[].name'
```

If "Olve.Pipelines v2" exists on either instance — **stop**: `pl binding create` mints a new id
every time and would create a second self-deployer.

## Step 3: Validate the pipeline against this instance

```bash
(cd src/sdk && bun install) && (cd .pipelines && bun install)
pl dev validate --dir .pipelines            # describe locally + the service's reconcile checks
```

Expect `valid` (`output-unused` warnings on deploy-prod and publish-* are fine).

## Step 4: Create the pipeline bound to the repo

The build Job fetches the repo tarball with the `GITHUB_TOKEN` pipeline secret. v2 prod has no
public webhook route, so it polls (every 15 minutes).

```bash
PID=$(pl binding create OliverVea/Olve.Pipelines --branch main --path .pipelines --credentials-secret GITHUB_TOKEN --json | jq -r .pipelineId)
echo "Pipeline: $PID"
```

## Step 5: Set the secrets

`pipeline.ts` declares `GITHUB_TOKEN` (repo tarball), `SSH_PRIVATE_KEY` (image import + helm on
the host) and `NPM_TOKEN` (`publish-sdk`). Values live in `olve-pipeline-<id without dashes>` in
`olve-runners`. Copy them from the previous pipeline's Secret (if it survived the reset).
`olve-runners` holds every pipeline's Secret, so pick the old deployer's by its id:

```bash
ssh oliver@bulwark-m2 "kubectl get secrets -n olve-runners | grep olve-pipeline-"
OLD_SECRET="olve-pipeline-<old id without dashes>"
for KEY in GITHUB_TOKEN SSH_PRIVATE_KEY NPM_TOKEN; do
  ssh oliver@bulwark-m2 "kubectl get secret $OLD_SECRET -n olve-runners -o jsonpath=\"{.data.$KEY}\"" \
    | base64 -d | pl secret set "$PID" "$KEY"
done
```

Pipe the bytes straight in — never through `$(…)`, which strips the SSH key's trailing newline.
If there is no previous Secret, set `GITHUB_TOKEN` to a GitHub read token, `SSH_PRIVATE_KEY` to the
homelab deploy key and `NPM_TOKEN` to the npm publish token out-of-band, the same way (stdin).
Never print the values.

## Step 6: Reconcile

```bash
pl binding reconcile "$PID"                 # returns at once; the build Job takes a minute or two
pl binding status "$PID"                    # repeat until Reconcile: Success
pl pipeline versions "$PID"                 # version 1, linked to the latest .pipelines/ commit
pl pipeline manifest "$PID"                 # source → build, test → deploy-beta → beta-test → when deployTo=prod: deploy-prod → publish-cli, publish-sdk
```

`Error` → read the problems (`pl dev validate` reproduces manifest problems). The first reconcile
only records the branch head; it does not deploy.

## Step 7: Verify a deploy

Run `/deploy` (manual trigger with the current `v2` head), or push to `v2` and wait for the deploy
poll (up to 15 min). Until `publish-cli` has succeeded once, prod's `/download` and `/install.sh`
serve no `pl`.

## After setup

Tell the user: the pipeline id, the binding (repo@branch, path, trigger), the reconcile result and
the secrets' state; that pushes to `v2` now deploy v2 beta and then v2 prod automatically; that pipeline changes go through
`.pipelines/pipeline.ts`; and that `/deploy` starts a run by hand.
