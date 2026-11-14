# CI: vision

Olve.Pipelines does CD today: a pipeline runs on pushes to its bound branch. CI is a second, separate part of the same project.
It shares the repo's binding, its secrets and step library, but has its own definition, its own runs and its own specification.

## What CI adds

- **Change runs.** A pull request's pushes start CI runs for its head commit. Each PR is its own scope for latest-wins: a newer push supersedes only that PR's older run, never a CD run or another PR's.
- **Results on the forge.** A CI run reports to GitHub (and later Gitea/Forgejo) as check runs with a short summary of what failed. Agents read the result with `gh` alone.
- **Schedules.** A cron trigger for CI and CD (e.g. weekly dependency updates), so nobody needs an outside CronJob or a poll on a clock URL.
- **Forge actions as steps.** Library steps to open a PR, comment on it and merge it when its CI passes. Together with schedules, they replace a dependency bot.
- **Shared steps, not shared runs.** CI and CD import the same steps (build, test). A CI run never deploys; CD never runs for a PR.

## Open questions (for the spec)

- Definition shape: a second entry point in `.pipelines/` (`ci.ts`) vs. `pipeline({ ci, cd })`.
- Untrusted heads (fork PRs): no secrets, or none at all.
- Whether a CD run can reuse a merged PR's CI outputs (build once).

## First user

Olve.Trains moves its PR checks here from GitHub Actions (its Agentic CI epic).
