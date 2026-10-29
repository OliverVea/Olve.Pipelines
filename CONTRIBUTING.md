# Contributing

How the codebase is laid out and built. Using Olve.Pipelines: [docs/setup](docs/setup/index.md).
Design: [docs/design/pipelines-v2.md](docs/design/pipelines-v2.md) (spec),
[docs/design/pipelines-v2-plan.md](docs/design/pipelines-v2-plan.md) (what was built), and
[docs/design/roadmap.md](docs/design/roadmap.md). Agents: [CLAUDE.md](CLAUDE.md), [AGENTS.md](AGENTS.md).

## Project structure

```
src/
├── backend/                        The .NET projects (the solution, build props and global.json stay at the root)
│   ├── Olve.Pipelines/             API + scheduler (minimal API, Native AOT)
│   │   ├── Configuration/          Auth, telemetry, JSON, host, storage, DI wiring, the generated API's handlers
│   │   ├── Distribution/           /download/{asset} (latest publish-cli files), /install.sh|.ps1
│   │   ├── GitHub/                 GitHub client, push webhook payload + signature
│   │   ├── Health/                 /api/health, /api/ready
│   │   ├── Jobs/                   JobRunner, K8s executor, obsoletion, job (attempt) endpoints
│   │   ├── Kubernetes/             K8s client, pipeline Secrets, in-cluster credentials
│   │   │   └── Harness/            step / build / map-trigger / failure-handler Job specs, result collection
│   │   ├── Pipelines/              Pipeline entity, summaries, health, storage layout
│   │   │   ├── Failures/           onFailure dispatch, FailureContext
│   │   │   ├── Manifests/          manifest parser, validator (reconcile checks), entity versioning, JSON Schema
│   │   │   ├── Retention/          run/binary/leftover sweeps
│   │   │   ├── Runs/               Run, Attempt, run/attempt services, run endpoints, views
│   │   │   ├── Scheduling/         StageScheduler, run start (de-dup), continue/rerun, cancel, blocked steps, switch
│   │   │   ├── Sync/               bindings, deploy poll, reconcile, binding webhooks, config source
│   │   │   ├── Triggers/           push / poll / manual triggers, map-trigger, input validation
│   │   │   └── Versions/           PipelineVersion, activation (Active / Draining), version endpoints
│   │   └── Shared/                 AttachmentStore, startup runner, persistence, S3 presigning
│   └── Olve.Pipelines.UnitTests/   Unit tests (TUnit + Rocks)
├── spec/                           The API contract (TypeSpec, one .tsp per area); bun run build → artifacts/ (gitignored): openapi.json, the server's generated C#, the TypeScript client, the API reference (docs/design/spec-first.md)
├── sdk/                            @olivervea/pipelines: authoring API, harness, olve-build, olve-dev (Bun)
├── harness/                        harness-base init image + end-to-end validation
├── client/                         @olve/client: the setup over the generated client both clients share (bearer, ApiError, typed SSE streams)
├── cli/                            `pl` operator CLI (Bun + TypeScript on the generated client, compiled per platform)
├── frontend/                       Dashboard UI (Lit + Vite + TypeScript)
└── deploy/
    ├── helm/                       Helm chart (values.yaml, values-beta.yaml, values-v2-beta.yaml, values-v2-prod.yaml, values-minimal.yaml)
    └── compose/                    The throwaway local instance's config and deployer (docker-compose.yml at the root)
.pipelines/                         This repo's own pipeline (v2) + the paused v1 config.yaml
.pipelines-test/                    v2 fixture pipeline (bound on v2 beta)
docs/setup/                         Setup guide, served at /docs
docs/design/                        v2 spec, plan, spike findings (spike code: git history, `d269107:spikes/`)
tools/version.cs                    CalVer versioning script
```

## Build & test

```bash
dotnet build                                                   # Build (compiles src/spec/ first; needs bun)
dotnet test                                                    # Tests
dotnet run --project src/backend/Olve.Pipelines                        # Run locally

cd src/spec && bun install && bun run build                    # The contract alone: artifacts/spec/openapi.json, artifacts/generated/backend/*.g.cs and the TypeScript client artifacts/clients/ts
cd src/sdk && bun install && bun run check                         # SDK: prettier, type-check, compile-fail cases, runtime tests
cd src/frontend && npm install && npm run build                    # Frontend (tsc + vite)
```

The server build runs `bun run compile` in `src/spec/` when the contract changed, and compiles the
generated `artifacts/generated/backend/*.g.cs`; `-p:SkipSpecGen=true` compiles already-generated
files without bun (the deploy's `spec` step emits them for `compile-server` and `test`).

### Check before you push

`tools/check.sh` runs what the deploy's `test`, `build-sdk` and `build-frontend` steps run, per
project (SDK, `.pipelines`, frontend, .NET server + CLI with their generated files):

```bash
tools/check.sh                 # fast tier: formatting, lint, type checks, the spec compiles, generated files current, pipeline valid
tools/check.sh --changed       # only the projects changed against the upstream branch (plus uncommitted work)
tools/check.sh --full          # plus the unit tests, the SDK test suite and the frontend build
tools/check.sh --fix           # format and regenerate first (prettier, dotnet format, manifest schema)
```

Formatting: prettier for all TypeScript (`.prettierrc` at the root; `*.md`, generated files and the
SDK's compile-fail fixtures are ignored), `dotnet format` for C#. Agent sessions run
`tools/check.sh --hook` before every `git push` (a `PreToolUse` hook in `.claude/settings.json`);
for your own pushes, `git config core.hooksPath .githooks` installs the same gate as a git
`pre-push` hook.

Every deploy checks the fresh v2 beta in the `beta-test` step (`.pipelines/handlers/beta-test.ts`):
readiness, the API's reads and error shape, that an anonymous mutation is refused, the event stream,
the frontend bundle and the install script.

## Logging conventions

Structured `ILogger<T>` with named placeholders; log human-readable names next to ids. Failures
are logged with `ILogger.LogProblems(...)` (`src/backend/Olve.Pipelines/Shared/ResultProblemLogExtensions.cs`),
which flattens a `ResultProblem` set into `ProblemCount` / `ProblemMessages` / `MaxSeverity`.
What an operator sees: [docs/setup/self-hosting.md](docs/setup/self-hosting.md#logging).

## The contract, its clients and the API reference

The API is the TypeSpec contract in `src/spec/` (one `.tsp` per area). `bun run build` there, which
the server build runs when the contract changed, writes everything derived from it under the
gitignored `artifacts/`: the OpenAPI document, the server's generated C# surface (operation
handlers, models, JSON context) and the TypeScript client `@olve/api` (Hey API). Nothing generated is committed; the deploy's `spec` step emits the same files for the build
steps, and the frontend type-checks against the client.

An API change is a `.tsp` change plus the handler class behind the operation (one per operation,
in the area's folder, registered in `ApiHandlerConfiguration`). The server refuses to start while
an operation has no handler, and a handler can only answer the statuses its operation declares.

The contract is the API's documentation: an operation's `@summary`, its doc comment and `@useAuth`
(bearer everywhere unless `NoAuth`). The server serves the compiled OpenAPI document at
`/docs/openapi.json`. The routes outside it (the step file download, the inbound hooks) are
described in the setup pages of the features they belong to.

### Clients

There is no C# client. The frontend and `pl` (`src/cli/`) both use the generated TypeScript client.

## Versioning

```bash
dotnet run tools/version.cs                                          # 0.0.0-dev+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42                  # 2026.3.28.42+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42 --rid linux-x64  # artifact name
```
