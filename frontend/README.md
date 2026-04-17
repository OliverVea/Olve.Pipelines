# Olve Pipelines Frontend

Dashboard UI for the Olve.Pipelines CD orchestration service. Visualizes pipelines as left-to-right flow diagrams with live job status on each step.

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
npm run build        # Production build to dist/
npm run preview      # Preview production build
npm run typecheck    # Type-check src/ (Kiota client excluded — known TS issues in generated code)
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

## Project Structure

```
frontend/
├── index.html                          # Entry point — mounts <app-shell>
├── package.json
├── vite.config.ts                      # Vite config with API proxy
├── tsconfig.json                       # Base TS config
├── tsconfig.app.json                   # App-only type-checking (excludes client)
├── eslint.config.js
├── .prettierrc
└── src/
    ├── main.ts                         # Component imports (side-effect registration)
    ├── api.ts                          # Kiota client setup
    ├── router.ts                       # Client-side router (~30 lines)
    ├── styles/
    │   └── theme.css                   # Global CSS variables and reset
    └── components/
        ├── app-shell.ts                # Top-level layout, route switching
        ├── pipeline-list-view.ts       # Pipeline list with create/delete
        ├── pipeline-detail-view.ts     # Loads pipeline data, renders flow
        ├── pipeline-flow.ts            # Flow diagram layout (columns + connectors)
        └── step-node.ts               # Single step card with job status badge
```

## Pipeline Flow Visualization

The detail view renders the pipeline as a horizontal flow:

```
┌──────────────┐                        ┌──────────────┐    ┌──────────────┐
│ Prod Step A  │                        │              │    │              │
│   ● Done     │   ┌───────────────┐    │ Proc Step 1  │    │ Proc Step 2  │
├──────────────┤──▶│ ArtifactBundle│──▶ │   ⟳ Running  │──▶│   — Idle     │
│ Prod Step B  │   └───────────────┘    │              │    │              │
│   ● Done     │                        └──────────────┘    └──────────────┘
└──────────────┘
  (parallel)                                  (sequential, ordered)
```

- **Production steps** stack vertically (run in parallel)
- **Processing steps** chain left-to-right (run sequentially)
- Each step node shows the latest job status: Idle, Scheduled, Running, Done, Failed, Cancelled, Obsolete

## API Client

The frontend uses a Kiota-generated TypeScript client from `clients/olve-pipelines-client-ts/`, linked via `file:` reference. To regenerate after API changes:

```bash
# From repo root — rebuild the backend to update api.json, then:
kiota generate -l typescript -d api.json -o clients/olve-pipelines-client-ts/src -n OlvePipelinesClient --clean-output

# Then install client deps
cd clients/olve-pipelines-client-ts && npm install
```

## Authentication

Not yet implemented. The backend supports JWT Bearer auth via Authentik (OIDC). For local development, auth is disabled via `Auth:Disable=true` in the backend configuration.
