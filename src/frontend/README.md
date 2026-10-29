# Olve Pipelines Frontend

The web UI of Olve.Pipelines: pipelines as sequential stages of parallel steps, runs and attempts
with typed inputs and outputs, versions, config, secrets, bindings, blocked steps and step history,
plus the setup docs at `/docs`.

## Stack

| Concern | Choice |
|---------|--------|
| Language | TypeScript |
| UI | [Lit](https://lit.dev/) (Web Components) |
| Build | [Vite](https://vite.dev/), two entries: the app (`index.html`) and the API reference (`api-reference.html`, [Redoc](https://redocly.com/redoc)) |
| API client | `@olve/client` over `@olve/api`, the [Hey API](https://heyapi.dev) client generated from the contract in `src/spec/` |
| Linting | ESLint + typescript-eslint |
| Formatting | Prettier (`.prettierrc` at the repo root) |

## Commands

The generated client lives in the repo-root `artifacts/` (gitignored): run `cd src/spec && bun run
build` once, and again after the contract changes.

```bash
npm install          # Install dependencies
npm run dev          # Dev server with hot reload (proxies /api/ to localhost:5000)
npm run dev:prod     # Dev server proxying /api/ to https://pipelines-private.ovea.pro
npm run dev:beta     # Dev server proxying /api/ to https://pipelines-beta.ovea.pro
npm run build        # Type-check (tsc) + production build to dist/
npm run preview      # Preview the production build
npm run typecheck    # Type-check src/, the shared client and the generated client
npm run lint         # Lint src/
npm run format       # Format src/ with Prettier
npm run format:check # Check formatting
```

`npm run dev` and `npm run build` first index `docs/setup` for the docs search
(`scripts/build-docs-index.mjs`).
`VITE_API_TARGET` picks any other instance; the `prod-api` and `beta` modes set it in `.env.prod-api`
and `.env.beta`. The proxy keeps the API same-origin, so the backend needs no CORS configuration.

## Development

### Against a local backend

Run the server with auth disabled (`Auth:Disable=true`, e.g. via `dotnet user-secrets`), then the
dev server:

```bash
dotnet run --project src/backend/Olve.Pipelines   # from the repo root; listens on :5000
cd src/frontend && npm run dev
```

### Against beta or prod

Signing in needs a redirect URI the instance's OIDC client allows, which a deployed instance
doesn't have for localhost. Use the dev-only auth bypass instead (`VITE_DEV_AUTH=bypass`, ignored
by production builds since `import.meta.env.DEV` gates it). It skips the OIDC redirect, shows a
placeholder user and sends `VITE_DEV_TOKEN` as the bearer when set:

```bash
VITE_DEV_AUTH=bypass npm run dev:beta                       # beta's reads are anonymous
VITE_DEV_AUTH=bypass VITE_DEV_TOKEN=<jwt> npm run dev:beta  # with a bearer token for actions and files
```

Against prod (`dev:prod`) every call needs the token, and anything you do changes real pipelines.

## Project structure

```
src/frontend/
├── index.html, api-reference.html  # The two Vite entries
├── scripts/build-docs-index.mjs    # docs/setup → public/docs-index.json (docs search)
└── src/
    ├── main.ts                     # Component imports (side-effect registration)
    ├── api-reference.ts            # Redoc over /docs/openapi.json, embedded by docs-view
    ├── auth.ts                     # OIDC (oidc-client-ts) from /api/config/frontend; the dev-only bypass
    ├── router.ts                   # Client-side routes and paths
    ├── api/
    │   ├── client.ts               # The contract's client (`api`), error messages
    │   ├── events.ts               # LiveController: views reload on change notices
    │   └── files.ts                # downloadFile: a step file to the browser
    ├── model/
    │   ├── manifest.ts             # Manifest types, mapping leaves, when evaluation, canonical JSON
    │   ├── versions.ts             # Version cache, activation (active / draining)
    │   ├── config-schema.ts        # Config document schema, validation, completion lookups
    │   ├── schema-form.ts          # Input model JSON Schema → form fields + validation
    │   └── diff.ts                 # Line LCS diff with folded context
    ├── styles/
    │   ├── theme.css               # Global tokens (light + dark) and reset
    │   └── ui.ts                   # Status tones and labels, formatting, shared component styles, pipeline tabs
    └── components/
        ├── app-shell.ts            # Layout, sign-in, route switching
        ├── pipeline-list-view.ts   # Pipelines with run health; add a pipeline
        ├── pipeline-header.ts      # Name, health, description and tabs of every pipeline page
        ├── pipeline-detail-view.ts # Overview: stage graph with each step's latest attempt, step sheet
        ├── pipeline-runs-view.ts   # A pipeline's runs
        ├── pipeline-versions-view.ts # Versions tab (versions-panel)
        ├── pipeline-config-view.ts # Config document: editor, last save, snapshots with rollback, diff
        ├── pipeline-secrets-view.ts # Secrets: who uses each, set / delete / copy
        ├── pipeline-binding-view.ts # Git binding: definition location, reconcile status, credentials
        ├── critical-path-view.ts   # Where a range of runs spent its time
        ├── run-view.ts             # One run: graph by status, attempts with typed I/O, files, logs, actions
        ├── step-logs-view.ts       # One step's log in a run, full height
        ├── step-runs-view.ts       # One step's attempts across runs, with a status filter
        ├── docs-view.ts            # The /docs setup guide, its search and the API reference
        ├── stage-graph.ts          # Stage graph at two levels (chips, cards), oriented by the viewport
        ├── step-sheet.ts           # Bottom sheet with one step's details
        ├── trigger-panel.ts        # Manual trigger form + run plan preview
        ├── input-form.ts           # Form from an input model's JSON Schema
        ├── external-input-panel.ts # A waiting step's external inputs: answer or decline
        ├── versions-panel.ts       # Versions list, manifest diff, switch
        ├── config-editor.ts        # YAML editor with schema errors and completion
        ├── diff-view.ts            # Folded line diff
        ├── typed-value.ts          # JSON rendered against its schema (file refs downloadable)
        ├── log-pane.ts             # Attempt logs, streamed while running
        ├── phase-bar.ts            # An attempt's time by phase
        ├── relative-time.ts        # Relative / absolute timestamp toggle
        ├── clamped-text.ts         # One-line text with a "more" toggle
        └── confirm-delete-dialog.ts # Type `delete <name>` to confirm
```

## Views

- **Pipelines** (`/`): per pipeline its run health, active version or a switch in progress,
  running and held runs, blocked steps and the per-step strip.
- **Pipeline** (`/pipeline/:id`): a header with tabs (Overview, Runs, Versions, Config, Secrets,
  Binding when bound). The overview shows the active version's stage graph with each step's latest
  attempt. The graph has two levels, Chips and Cards (`[` / `]`, remembered per browser). A
  landscape viewport lays stages out left to right (scrolling sideways when too wide), a portrait
  one top to bottom. `when` blocks are dashed brackets labelled with their condition. Hovering a
  step outlines where its inputs come from and what reads it. Clicking a step opens its details in
  a bottom sheet (`?step=`): status, reason, latest attempt, recent runs, what it reads and what
  reads it, its definition, and the actions (logs, cancel, rerun and continue, block / unblock,
  the step's runs).
- **Run** (`/pipeline/:id/run/:n?step=&attempt=`): a one-line header (run, status, duration, a
  Details fold with rerun, continue, version and input; Cancel stays on the line while running)
  over the pinned version's graph coloured by step status. The selected step's sheet holds its
  attempts with typed input and output, files, live logs and external inputs, and cancel / rerun
  step (with or without continue).
- **Step log** (`/pipeline/:id/run/:n/step/:name/logs`) and **step runs**
  (`/pipeline/:id/step/:name/runs`): one attempt's log full height; a step's attempts across runs.
- **Critical path** (`/pipeline/:id/critical-path`): each step's share of a range of runs' time.
- **Docs** (`/docs/:page`): `docs/setup`, rendered with search. `/docs/api` is the API reference
  when the instance sets `Docs:ApiReference`.

## API usage

Every call goes through the generated client: `pipelinesList({ client: api })` returns `{ data }`,
and a non-2xx answer throws `ApiError` (from `@olve/client`) with every problem message;
`errorMessages(e)` turns any thrown value into lines to show. Dates arrive as ISO strings. There
are no hand-built `/api/` URLs: `tools/check.sh` rejects them. `@olve/client` (`src/client/`,
shared with `pl`) sets up the client and reads the event streams typed.

Views refresh on change events, not on a timer: `LiveController` (`src/api/events.ts`) reloads its
view when a matching notice arrives (debounced), on every (re)connect or `resync`, and every 60 s
as a safety net. One stream serves the tab, open while a view listens and the tab is visible.

## Authentication

OIDC authorization code flow via `oidc-client-ts` (`src/auth.ts`) against the instance's provider,
the bundled Dex by default. The authority, client id and scopes come from `GET /api/config/frontend`,
fetched anonymously before sign-in. The access token goes as a bearer token on every API call.
