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
npm run build        # Production build to dist/
npm run preview      # Preview production build
npm run typecheck    # Type-check src/ (Kiota client excluded — known TS issues in generated code)
npm run lint         # Lint src/
npm run format       # Format src/ with Prettier
npm run format:check # Check formatting
```

## Development

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
