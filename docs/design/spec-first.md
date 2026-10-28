# Spec-first API: TypeSpec

Decisions taken with Oliver on 2026-10-27, when the v2 roadmap had no pressing feature left. The
HTTP API moves from code-first (minimal APIs → `api.json` → Kiota) to a TypeSpec contract that
generates the OpenAPI document, the server's C# surface and the TypeScript client. The reference
implementation is Olve.AgentRuntimeManager (ARM): `docs/SPEC-FIRST.md` there, emitter
`src/codegen/typespec-arm-csharp`, runtime `src/backend/Olve.AgentRuntimeManager/Api/`.

## Why

- Reading the spec shows what the API does; 56 operations spread over eleven `*Endpoints.cs` files
  don't.
- Typing gaps close: the frontend's eight mutations bypass the Kiota client with raw `fetch`
  (Kiota flattens the error array, can't send a `JsonElement` body, escapes `/` in the file
  catch-all), and `pl` duplicates every contract record by hand.
- Conformance is proven once, in the generator's suite, instead of per service.

## Decisions

| Question | Decision |
|---|---|
| Where the emitter and its runtime live | **Olve.Utilities**, both: the runtime as a NuGet package next to `Olve.MinimalApi`, the emitter as an npm package in the same repo. ARM switches to the packages and deletes its copies. |
| How far the spec drives the server | **Full generator**, ARM-style: records, response unions, one handler interface per operation, the route map, validation and the JSON source-gen context are generated; handlers are hand-written classes over the existing domain services. |
| Migration | **Area by area on `main`**, each area a deployed push, and each area's spec **fixes that area's warts** as it goes (frontend and `pl` follow in the same push). Generated routes and old minimal APIs coexist until the last area. |
| Frontend client | **Hey API** from the spec's OpenAPI, as in ARM. Kiota, the committed client and `apiFetch` go. |
| `pl` | **Rewritten in TypeScript on Bun**, modelled on ARM's `arm` CLI, as the last step, once the spec covers every area. It shares the Hey API client with the frontend and imports the SDK's dev tooling instead of shelling out to it. |
| Generated output | Never in source folders: `artifacts/` (gitignored), as in ARM. `api.json`, the Kiota client and `docs/setup/api-reference.md` stop being committed at the end of phase 2. |
| Olve.Template.Api | Follows about a week after the Pipelines migration has settled, on the same packages. |

Alternatives considered: contract-only (spec emits OpenAPI, endpoints stay hand-written, a test
compares `api.json` to the spec) keeps the typing gaps; a C# client mode in the emitter would have
one user (`pl`); Go for `pl` is a third language and a third generator with nothing shared.

## Shape after the migration

```
src/spec/main.tsp  (one .tsp per area: instance, pipelines, versions, config, secrets, bindings, steps, runs, attempts, triggers, events)
  │  tsp compile (bun)
  ├─► artifacts/spec/openapi.json ─► Hey API ─► artifacts/clients/ts ─► frontend, cli/
  │                                └─► tools/api-reference ─► wwwroot/docs/api-reference.md (served at /docs)
  └─► @olivervea/typespec-csharp ─► artifacts/generated/backend/*.g.cs ─► src/backend/Olve.Pipelines (handlers hand-written)
```

The deployer gets a `spec` step on `oven/bun` whose output (the `.g.cs` files and `openapi.json`)
feeds `compile-server`, `build-frontend` and `build-cli`, so the .NET and Node images need no extra
tooling (ARM does the same with `-p:SkipSpecGen=true` in its Docker .NET stage).

## Phase 0: tooling moves to Olve.Utilities

Do this first, verified against ARM (the existing consumer), before Olve.Pipelines uses any of it.

**Done 2026-10-28**: `Olve.TypeSpec` and `@olivervea/typespec-csharp` 0.58.0 are published
(Olve.Utilities #94, conformance suite `tests/Olve.TypeSpec.Conformance`, skill `olve-typespec`),
and ARM builds on them with its own copies deleted. The emitter work Olve.Pipelines needs
(auth per operation, binary responses, `Id<T>` parameters, offset/limit pagination) is still open
and lands with the area that first needs it. Follow-ups: Olve.Utilities #97 (MSBuild targets +
handler-registration source generator), #98 (PublishAot smoke test), #99 (skill evals).

- **Runtime NuGet** (name to settle in that repo, e.g. `Olve.TypeSpec`): what ARM's `Api/` holds
  today, with the `Arm` prefix dropped: handler and response interfaces, the operation descriptor,
  the SSE result and item, validation, binding-failure middleware, the `UseApi` wiring with the
  missing-handler check. The error envelope stays **pluggable** (ARM already has `external-types`),
  so Olve.Pipelines keeps `ApiProblem` and its status precedence.
- **Emitter npm package** (`@olivervea/typespec-csharp`): ARM's `collect.js` / `render.js` /
  `names.js`, its snapshot tests and the .NET conformance suite, published from Olve.Utilities'
  GitHub Actions like the NuGet packages.
- **Emitter work Olve.Pipelines needs** (ARM doesn't):
  - **Auth per operation**: `@useAuth` on an operation or interface → `RequireAuthorization(policy)`
    on the generated route, with the policy names given in `tspconfig.yaml`. Olve.Pipelines has a
    read policy (anonymous when `Auth:AnonymousReads`) and a write policy.
  - **Binary responses**: `GET …/files/{path}` streams a file; the emitter must accept a
    `bytes`/stream response and a `{*path}` catch-all, or that one operation stays hand-written and
    is declared in the spec for the clients only.
  - **`Id<T>` route parameters**: a decorator (or `@format`) that binds `{pipelineId}` as
    `Id<Pipeline>`, not `Guid`.
  - **Pagination** is offset/limit: a list takes `offset` and `limit` and answers
    `{ items, total }`. The page-number model (`Olve.Utilities.Paginations`' `Pagination` and
    `Page<T>`) is deprecated there; a page is a UI notion, computed by the client.
  - SSE unions already work (`@events` with a `type` literal per variant); `/api/events` notices
    and the log stream must carry one.
- ARM switches to the packages in its next release; its `src/codegen/` is deleted.

## Phase 1: the spec skeleton in Olve.Pipelines

- `src/spec/` with `main.tsp`, `tspconfig.yaml`, a `package.json` (compiler, `@typespec/http`,
  `openapi3`, `sse`, `events`, the emitter) run with bun; `artifacts/` gitignored.
- The server project compiles `artifacts/generated/backend/*.g.cs` and runs `tsp compile` from
  MSBuild unless `SkipSpecGen` (ARM's target). `AddApi`/`UseApi` wired beside the existing
  `Map*Endpoints`.
- `tools/check.sh` gains the spec compile and the generated-code check; the deploy's `test` step
  keeps the `api.json` / Kiota / reference checks until phase 2 ends.

**Built 2026-10-28**, together with the Instance area (the emitter's JSON context doesn't compile
for a contract without operations, so the skeleton carries the first area): `src/spec/` (bun),
`artifacts/` gitignored, the `GenerateApi` MSBuild target with `SkipSpecGen`, `AddPipelinesApi` /
`UsePipelinesApi` beside the old endpoints, `ApiProblemWriter` as the `IApiErrorWriter`, a `spec`
step in the deployer (its `artifacts/` feed `compile-server` and `test`), `tools/check.sh` with
the spec compile. Found on the way, fixed in Olve.Utilities 0.59.0 (PR #100): the generated code
used a reflection-based `TypedResults.Json` overload the AOT analyzers reject (ARM is JIT, so it
never showed); the conformance suite now builds with the AOT analyzers and runs without
reflection-based JSON, where the request delegate generator's bare 400 for a bad optional
parameter also surfaced. What the contract can't say yet is applied app-side in
`ApiHandlerConfiguration.MapApi`: `AllowAnonymous` and the OpenAPI tag and summary of each
operation (`api.json` still feeds the API reference); emitting those from the spec is emitter work
for a later area. The frontend keeps the Kiota client for this area (same routes and shapes); the
Hey API switch comes with the first area whose typing it improves (Pipelines).

## Phase 2: areas, one push each

Order, smallest and least coupled first, so the pattern settles before the big ones:

1. Instance (3 operations: health, ready, the login settings) — done with phase 1
2. Pipelines (5)
3. Versions (5)
4. Secrets (4)
5. Config (5)
6. Bindings (8)
7. Steps (14)
8. Runs (9) and Attempts (1)
9. Triggers (1)
10. Events (1, SSE) and the log stream

Each push carries: the area's `.tsp` with its warts fixed, one handler class per operation calling
the existing service (the endpoint lambdas' bodies move, the services don't change), the frontend's
calls for that area on the Hey API client, `pl`'s hand-rolled contracts updated to the new shapes,
docs. `beta-test` covers it before prod. The old `*Endpoints.cs` for the area is deleted in the
same push; `api.json` stays build-generated meanwhile because the generated routes are minimal APIs
too.

After the last area: `api.json`, `src/clients/olve-pipelines-client-ts/`, the Kiota tool pin and the
committed `docs/setup/api-reference.md` are removed; the reference is generated into `wwwroot` at
build from the spec's OpenAPI; the `test` step checks that the spec compiles and the frontend
builds against it.

**Wart list** (grows as areas are specified; fixed in the area's push):

- `{id}` on `GET`/`DELETE /api/pipelines/{id}` vs `{pipelineId}` everywhere else.
- Status fields are strings (`Run.status`, `Attempt.status`, `TriggerRunResponse.outcome`); make
  them enums.
- `POST /api/pipelines/with-repo`: name it for what it does.
- `TriggerRunRequest.fields` is a free `JsonElement`; the spec says `Record<unknown>` and the body
  is the input object itself where the client already has it.
- `GET /api/attempts?status=active` is the only top-level cross-pipeline view; decide if it stays.
- `GET …/runs` pages with `page`/`pageSize` into `PageOfRunSummary` (`pageNumber`, `pageSize`,
  `totalCount`); step history takes `limit`. Both become `offset`/`limit` with `{ items, total }`
  (decided 2026-10-27: pages exist only in the UI).

## Phase 3: `pl` in TypeScript

- `cli/` beside `src/sdk/` and `src/frontend/`, Bun, compiled with `bun build --compile` for
  `bun-linux-x64` and `bun-windows-x64` (the two `build-cli*` steps become one bun step).
- Taken from `arm`: the OIDC browser and device flows with the terminal QR code, credential storage,
  the client wrapper with the error envelope, SSE tailing with reconnect and `Last-Event-ID`,
  `--json` output, the command registry and help.
- Kept from today's `pl`: the `~/.pl` profile file format (existing logins survive), command names
  and exit codes (`pl wait`: 0/1/2/3/124, Ctrl-C 130), `pl dev` (now importing `olve-dev` from the
  SDK), `install.sh` / `install.ps1` and `/download`.
- `src/backend/Olve.Pipelines.Cli` is deleted. The `ovea-olve-pipelines` skill is updated and the plugin
  version bumped in the same work.

## Phase 4: Olve.Template.Api

About a week after phase 3 has settled on prod: the same packages, the same layout, so the template
is what a new service starts from.

## Open questions

- Package names in Olve.Utilities (`Olve.TypeSpec`? the npm scope is `@olivervea`).
- Whether the file download stays a hand-written endpoint declared in the spec for the clients.
- Whether `docs/setup/api-reference.md` keeps a committed copy for GitHub readers or is build-only.
