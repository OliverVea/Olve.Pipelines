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
│   ├── Olve.Pipelines.UnitTests/   Unit tests (TUnit + Rocks)
│   └── Olve.Pipelines.RealSystemTests/  Tests against a real server, Kubernetes and Garage (TUnit)
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
tools/real-system/                  The throwaway host the real-system tests run against
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
cd src/cli && bun install && bun test                              # pl (src/cli/README.md)
```

The server build compiles the contract when it changed (Olve.TypeSpec's `TypeSpecContract` target:
`tsp compile` in `src/spec/`) and compiles the generated `artifacts/generated/backend/*.g.cs`;
`-p:SkipSpecGen=true` compiles already-generated files without bun (the deploy's `spec` step emits
them for `compile-server` and `test`).

### Real-system tests

`src/backend/Olve.Pipelines.RealSystemTests` runs against a real server, Kubernetes and Garage. It isn't
part of `dotnet build` or `dotnet test` at the root. The tests don't start a host, so start one first:

```bash
tools/real-system/host.sh up                                   # a throwaway host on http://localhost:5000
dotnet run --project src/backend/Olve.Pipelines.RealSystemTests  # add -- --output Detailed for the host-size lines
tools/real-system/host.sh down                                 # remove it and its data
```

The header of `tools/real-system/host.sh` covers a pool of hosts (`TEST_TARGETS`, `TEST_TARGETS_FILE`), the
reset and the sweeper. The tests refuse a target whose cluster doesn't carry the host's marker Secret.

A test takes a host with `await using var server = await TestServer.LeaseAsync();`. It creates its own
pipelines (`server.NewPipelineAsync()`) and names anything else with `TestServer.NewName()`, asserts only
on what it owns, and never cleans up: retention with short thresholds and the sweeper do that. A disruptive
test is marked `[ExclusiveServer]`. Each contract in `Contracts/` runs the same assertions on the real
system here and on the fake in the unit tests (`Olve.Pipelines.UnitTests/RealSystem/FakeContractTests.cs`).

### Browser tests

`src/frontend/e2e` runs the main UI flows in Chromium ([Playwright](https://playwright.dev)) against the same
host, serving the production bundle. It is its own package, so neither `npm run build` nor the deploy installs it:

```bash
tools/real-system/host.sh up                                   # a throwaway host, as above
tools/real-system/host.sh ui                                   # after up: build the web UI into it; again after a frontend change
cd src/frontend/e2e && npm install && npx playwright install chromium
npm test                                                       # TEST_TARGETS picks the hosts, as above
```

The tests follow the rules above: each binds its own `e2e-` pipelines through the generated client, asserts only
on what it owns and never cleans up, and the run starts by applying the same guard to every target. Don't run
them on a host the real-system tests are using: those can reset it. The host has no sign-in, because its API has
auth off, so the tests seed the session the app would have stored after one; sign-in itself isn't covered. A test
fails on any uncaught error in the page. On a failure, `artifacts/real-system/browser/results` holds a trace for
`npx playwright show-trace`.

### Check before you push

`tools/check.sh` runs what the deploy's `spec`, `test`, `build-sdk`, `build-frontend` and `build-cli`
steps run, per project (SDK, `.pipelines`, the contract, frontend, `pl`, the .NET server), plus a rule
that neither client builds an `/api/` URL by hand:

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

Every deploy checks the fresh v2 beta in the `beta-test` step (`.pipelines/steps/beta-test.ts`):
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
in the area's folder; Olve.TypeSpec's source generator registers every handler class, and two
handlers for one operation don't compile). The server refuses to start while
an operation has no handler, and a handler can only answer the statuses its operation declares.

The contract is the API's documentation: an operation's `@summary`, its doc comment and `@useAuth`
(bearer everywhere unless `NoAuth`). The server serves the compiled OpenAPI document at
`/docs/openapi.json`. Every endpoint but the web UI is an operation, the step file download and
the inbound hooks included (`EndpointAuthTests` enforces it).

### Clients

The frontend (`src/frontend/`) and `pl` (`src/cli/`) call the server only through the generated
TypeScript client, set up by `@olve/client` (`src/client/`, see its README); `tools/check.sh`
rejects hand-built `/api/` URLs.

## Versioning

```bash
dotnet run tools/version.cs                                          # 0.0.0-dev+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42                  # 2026.3.28.42+cb9a99b
dotnet run tools/version.cs -- --ci --run-number 42 --rid linux-x64  # artifact name
```
