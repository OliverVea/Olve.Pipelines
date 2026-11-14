# CI: vision

Olve.Pipelines does CD today: a pipeline runs on pushes to its bound branch. CI is a second, separate part of the same project.
It shares the repo's binding, its secrets and step library, but has its own definition, its own runs and its own specification.

## What CI adds

- **Change runs.** A pull request's pushes start CI runs for its head commit. Each PR is its own scope for latest-wins: a newer push supersedes only that PR's older run, never a CD run or another PR's.
- **Results on the forge.** A CI run reports to GitHub (and later Gitea/Forgejo) as check runs with a short summary of what failed. Agents read the result with `gh` alone.
- **Schedules.** A cron trigger for CI and CD (e.g. weekly dependency updates), so nobody needs an outside CronJob or a poll on a clock URL.
- **Forge actions as steps.** Library steps to open a PR, comment on it and merge it when its CI passes. Together with schedules, they replace a dependency bot.
- **Shared steps, not shared runs.** CI and CD import the same steps (build, test). A CI run never deploys; CD never runs for a PR.

## Requirements from Olve.Trains' interim CI

Olve.Trains runs its PR checks on GitHub Actions with self-hosted runners in the homelab until this exists. Building that
showed what CI needs to do:

- **Cancel a PR's older run** when it gets a new push.
- **Skip by changed paths.** A docs-only PR skips the heavy steps but still reaches the steps after them, such as the merge.
- **Merge on green.** CI merges a PR itself when all its checks pass, the PR has opted in (a label, read when the checks
  finish, not when the run starts), and it doesn't touch protected paths that need a person's review. Without paid GitHub
  features there are no required checks or native auto-merge, so the merge is CI's job.
- **Merge only into the default branch.** A stacked PR waits until its base has merged and it has been retargeted. A retarget
  (`pull_request: edited`) starts a run, unlike pushes the forge makes itself.
- **Merge exactly what was tested:** the head commit the run checked.
- **CD on the merge.** A merge made by CI deploys like any other push.
- **Runner capacity** shares the cluster with CD. CI needs a concurrency limit, and its build steps run noticeably slower under
  gVisor than on GitHub's hosted runners.

## Open questions (for the spec)

- Definition shape: a second entry point in `.pipelines/` (`ci.ts`) vs. `pipeline({ ci, cd })`.
- Untrusted heads (fork PRs): no secrets, or none at all.
- Whether a CD run can reuse a merged PR's CI outputs (build once).

## First user

Olve.Trains moves its PR checks here from GitHub Actions (its Agentic CI epic).
