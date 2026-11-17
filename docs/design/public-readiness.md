# Ready for other people

What stands between Olve.Pipelines and someone else running their own instance, GitHub only. Ticked
items are done; the rest is the order they are taken in. Kubernetes and GitHub are the only things an
instance may depend on.

## 1. My setup is no default

- [x] `pl` has no default instance: `DEFAULT_API_URL` (`src/cli/src/profiles.ts`) is gone and a
      profile without a URL asks for `--api-url`.
- [ ] `helmDeployOverSsh` verifies the host key: it takes a known host key, and skipping the check
      is an explicit opt-out.
- [x] Examples use placeholders (`registry.example.com`, `pipelines.example.com`), not `ovea.pro`
      hosts, in `docs/setup`, the SDK library and examples, the frontend and the chart. Design docs and
      the repo's own `.pipelines/` describe this instance and keep them. The server has no default
      OIDC authority.
- [x] The README and the GitHub description say what it is (typed GitOps CI/CD on Kubernetes), with
      the homelab as where it comes from.

## 2. My workspace is apart from the product

- [x] Working notes leave the repo: `docs/superpowers/`, `docs/operations/`, `docs/design/review-2026-10-24.md`,
      `docs/design/test-audit.md`, `docs/design/quality-inventory.md`,
      `docs/design/pipelines-v2-spikes.md`.
- [ ] `docs/design/pipelines-v2-plan.md`'s still-true parts move into the design docs it backs.
- [x] `.claude/`: personal commands, skills and `settings.local.json` are untracked; the project's
      conventions (`CLAUDE.md`, `AGENTS.md`) stay, and the agent playbook is optional for contributors.
- [ ] `.pipelines-test/`, `tools/real-system/` and `src/harness/validate/` assume nothing about one
      machine or cluster.
- [x] `TestResults/` is ignored.

## 3. Installing is real

- [ ] The Helm chart is published to ghcr as a versioned OCI chart, so an install pins a release, not
      a git ref (`pl bootstrap` fetches it from a GitHub tarball of `--ref` today).
- [ ] A quickstart for a first real instance: a GitHub OAuth app for Dex, the allow-list, an Ingress
      with TLS, webhooks or polling. `pl bootstrap` asks for them, or one documented `values.yaml`
      holds them.
- [ ] A walkthrough of a first pipeline on a sample repo: binding, secrets, a push that deploys, a
      pull request that gets CI. Checked end to end on a fresh cluster.
- [ ] The GitHub token scopes a binding needs are documented.

## 4. Project hygiene

- [ ] Which SDK versions a server accepts is stated, and the SDK leaves beta or says what may still
      break.
- [ ] Release notes on the GitHub releases.
- [x] `SECURITY.md`: how to report a vulnerability in a service that runs other people's code.
- [x] Issue templates.
- [x] A contributor section in `CONTRIBUTING.md`: pull requests get CI from this service, only listed
      authors get it, so outside pull requests wait for a maintainer.
- [ ] What is supported, in one place: GitHub only, the Kubernetes versions, x86-64 step images,
      gVisor optional and recommended for untrusted code.
- [ ] Branch protection on `main` requires `olve/merge-queue` again.

## Not yet

A forge interface (GitLab and others), a plugin system, a GitHub App, multi-arch step images and a
docs site beyond what an instance serves at `/docs`: after one outside person has run an instance
from the docs.
