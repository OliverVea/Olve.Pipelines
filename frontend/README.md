# Olve Pipelines Frontend

Dashboard UI for the Olve.Pipelines CD orchestration service (v2 model): pipelines as sequential
stages of parallel steps, runs and attempts with typed inputs/outputs, versions, gates and step
history.

## Stack

| Concern | Choice |
|---------|--------|
| Language | TypeScript |
| UI | [Lit](https://lit.dev/) (Web Components) |
| Build | [Vite](https://vite.dev/) |
| API Client | [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/) (generated from `api.json`) |
| Linting | ESLint + typescript-eslint |
| Formatting | Prettier |

## Commands

```bash
npm install          # Install dependencies
npm run dev          # Dev server with hot reload (proxies /api to localhost:5000)
npm run dev:prod     # Dev server proxying /api to https://pipelines-private.ovea.pro
npm run dev:v2beta   # Dev server proxying /api to https://pipelines-v2-beta.ovea.pro
npm run build        # Type-check (tsc) + production build to dist/
npm run preview      # Preview production build
npm run typecheck    # Type-check src/ and the generated client it imports (also run by build)
npm run lint         # Lint src/
npm run format       # Format src/ with Prettier
npm run format:check # Check formatting
```

## Development

Two workflows are supported: run against a local backend, or run against the deployed production API.

### Against local backend

The Vite dev server proxies `/api` requests to `http://localhost:5000`. Run the backend locally with auth disabled:

```bash
# From repo root
dotnet run --project src/Olve.Pipelines
```

Then in a separate terminal:

```bash
cd frontend
npm run dev
```

### Against the prod API

`npm run dev:prod` starts Vite with `VITE_API_TARGET=https://pipelines-private.ovea.pro` (see `.env.prod-api`), so `/api` requests are proxied directly to production. No local backend is required.

```bash
cd frontend
npm run dev:prod
```

Notes:

- Auth uses the same Authentik OIDC client (`olve-pipelines-frontend` at `auth.ovea.pro`) as a deployed build. On that provider:
  - Add a **Strict** redirect URI `http://localhost:5173/callback` — this is load-bearing for CORS on the OIDC `.well-known` GET. A regex-mode entry alone is not enough: Authentik can only derive an allowed CORS origin from a strict URI with a concrete scheme+host+port.
  - Optionally also add a regex like `^http://localhost:\d+/callback$` so the redirect still works if Vite falls back to port 5174 (etc). CORS will only be allowed for the Strict ports.
- Keeping the API same-origin via the Vite proxy means no CORS configuration is needed on the backend and the browser won't prompt for the prod API's self-signed cert.
- Override the target with `VITE_API_TARGET=... npm run dev` if you need a different environment (e.g. beta).
- **Be careful**: this hits the real prod API. Any mutating actions you take will affect real pipelines and jobs.

### Against v2 beta, without signing in

The v2 beta OIDC client has no localhost redirect URI, so for local work against it use the
dev-only auth bypass (ignored by production builds — `import.meta.env.DEV` gates it):

```bash
VITE_DEV_AUTH=bypass npm run dev:v2beta                       # anonymous: every read works
VITE_DEV_AUTH=bypass VITE_DEV_TOKEN=<jwt> npm run dev:v2beta  # with a bearer token for actions
```

Reads (`GET`) are anonymous on the API; actions, file listing/downloads need a token.

## Project Structure

```
frontend/src/
├── main.ts                    # Component imports (side-effect registration)
├── api.ts                     # Kiota client (typed reads) + apiFetch/apiPost/apiText/downloadFile
├── auth.ts                    # OIDC (oidc-client-ts) + dev-only bypass
├── router.ts                  # Client-side router
├── styles/theme.css           # Global tokens (light + dark) and reset
├── v2/
│   ├── manifest.ts            # Manifest types, mapping leaves, when evaluation, canonical JSON
│   ├── schema-form.ts         # JSON Schema subset → form fields + validation
│   ├── diff.ts                # Line LCS diff with folded context (manifest diffs)
│   ├── versions.ts            # Version cache, activation (active/draining) helpers
│   └── ui.ts                  # Status tones/labels, formatting, shared component styles
└── components/
    ├── app-shell.ts           # Layout, auth gate, route switching
    ├── pipeline-list-view.ts  # Pipelines with run health
    ├── pipeline-detail-view.ts# Compact header, stage graph with latest attempts, trigger, step sheet
    ├── run-view.ts            # One run: compact header, coloured graph; step sheet with attempts, I/O, files, logs
    ├── step-history-view.ts   # A step's attempts across runs, definition-change markers
    ├── stage-graph.ts         # Stage graph at two levels (chips, cards), oriented by the viewport
    ├── step-sheet.ts          # Bottom sheet with one step's details
    ├── trigger-panel.ts       # Manual trigger form + run plan preview
    ├── input-form.ts          # Form generated from an input model's JSON Schema
    ├── versions-panel.ts      # Versions list, manifest diff, switch dialog
    ├── typed-value.ts         # JSON rendered against its schema (file refs downloadable)
    ├── log-pane.ts            # Attempt logs, polled while running
    └── docs-view.ts           # Serves the /docs setup guide
```

## Views

- **Pipeline list** — per pipeline: run health (last run), active version or a switch in progress,
  running and held runs, closed gates, and the per-step strip.
- **Pipeline** (`/pipeline/:id`) — a one-line header (name, health, Details fold, Trigger) over the
  active version's stage graph with each step's latest attempt. The graph has two levels, Chips and
  Cards (`[` / `]`, remembered per browser); a landscape viewport lays stages out left to right
  (scrolling sideways inside the graph when too wide), a portrait one top to bottom. `when` blocks
  are dashed brackets labelled with their condition; hovering a step outlines where its inputs come
  from and what reads it. Clicking a step opens its details in a bottom sheet (`?step=`): status,
  reason, latest attempt, recent runs, what it reads and what reads it, its definition, and the
  actions (logs, cancel, rerun and continue, block/unblock, the step's runs).
- **Run** (`/pipeline/:id/run/:n?step=&attempt=`) — a one-line header (run, status, duration,
  Details fold with rerun, continue, version and input; Cancel stays on the line while running) and
  the pinned version's graph coloured by step status. The selected step's sheet holds its attempts
  with typed input/output, files and live logs, and cancel / rerun step (with or without continue).
- **Step history** (`/pipeline/:id/step/:name`) — attempts across runs, with a marker where the
  step's definition (step version) changed.

## API usage

Typed reads go through the Kiota client. Actions, logs and file downloads use `apiFetch` in `api.ts`
instead, because the generated client maps a 400 to a single `ResultProblem` (the API returns an
array — `apiFetch` shows every message), sends `JsonElement` bodies via `setContentFromScalar` (fails
for objects) and would escape `/` in the file path catch-all. `JsonElement` members of responses
arrive as Kiota `UntypedNode`s — convert with `untypedToJson`.

## API Client

The frontend uses a Kiota-generated TypeScript client from `clients/olve-pipelines-client-ts/`, linked via `file:` reference. To regenerate after API changes:

```bash
# From repo root — rebuild the backend to update api.json, then:
dotnet tool restore && dotnet kiota generate -l typescript -d api.json -o clients/olve-pipelines-client-ts/src -n OlvePipelinesClient --clean-output

# Then install client deps
cd clients/olve-pipelines-client-ts && npm install
```

## Authentication

OIDC authorization code flow against Authentik via `oidc-client-ts` (`src/auth.ts`); the authority
and client id come from `GET /api/config/frontend`. The access token is sent as a bearer token on
every API call. For local development against a local backend, auth can be disabled via
`Auth:Disable=true` in the backend configuration; against a remote instance use the dev-only bypass
above.
