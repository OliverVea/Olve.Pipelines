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
npm run typecheck    # Type-check src/, mocks/, the shared client and the generated client
npm run lint         # Lint src/ and mocks/
npm run test         # Unit tests of the pure modules (vitest, test/)
npm run format       # Format src/, test/ and mocks/ with Prettier
npm run format:check # Check formatting
```

Browser tests of the main flows are in `e2e/`, a separate package: see [Browser tests](../../CONTRIBUTING.md#browser-tests).

UI mocks of features the server doesn't have yet are in `mocks/`: the real components on fixture data,
served by `npm run dev` only (e.g. `/mocks/matrix.html`), never part of the build.

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
VITE_DEV_AUTH=bypass VITE_DEV_TOKEN=<jwt> npm run dev:beta
```

Against prod (`dev:prod`) anything you do changes real pipelines.

## Project structure

```
src/frontend/
├── index.html, api-reference.html  # The two Vite entries
├── e2e/                            # Browser tests of the main flows (own package)
├── mocks/                          # UI mocks on fixture data (dev server only)
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
        ├── pipeline-runs-view.ts   # A pipeline's runs, each with its changes
        ├── pipeline-versions-view.ts # Versions tab (versions-panel)
        ├── pipeline-config-view.ts # Config document: editor, last save, snapshots with rollback, diff
        ├── pipeline-secrets-view.ts # Secrets: who uses each, set / delete / copy
        ├── pipeline-binding-view.ts # Git binding: definition location, reconcile status, credentials
        ├── critical-path-view.ts   # Where a range of runs spent its time
        ├── reliability-view.ts     # How reliable a pipeline's steps were
        ├── run-view.ts             # One run: status card, changes, graph by status, attempts with typed I/O, files, logs, actions
        ├── run-changes.ts          # A run's changes since the newest older run that succeeded
        ├── step-logs-view.ts       # One step's log in a run (or a CI run), full height
        ├── ci-view.ts              # CI tab: open / closed pull requests with their latest CI run, the merge queue
        ├── ci-run-view.ts          # One CI run: what it tests, its GitHub check, graph by status, the PR's runs
        ├── ci-parts.ts             # CI status dots, step strip and shared CI styles
        ├── step-runs-view.ts       # One step's attempts across runs, with a status filter
        ├── docs-view.ts            # The /docs setup guide, its search and the API reference
        ├── stage-graph.ts          # Stage graph at two levels (chips, cards), oriented by the viewport
        ├── step-sheet.ts           # Bottom sheet with one step's details
        ├── trigger-panel.ts        # Manual trigger form + run plan preview; prefilled for Trigger again
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
  running and held runs, blocked steps, pull requests failing or running CI, and the per-step strip.
- **Pipeline** (`/pipeline/:id`): a header with tabs (Overview, Runs, CI when the pipeline declares
  it, Versions, Config, Secrets, Binding when bound). The overview's status card counts open pull
  requests by their CI. The overview shows the active version's stage graph with each step's latest
  attempt. The graph has two levels, Chips and Cards (`[` / `]`, remembered per browser). A
  landscape viewport lays stages out left to right (scrolling sideways when too wide), a portrait
  one top to bottom. `when` blocks are dashed brackets labelled with their condition. Hovering a
  step outlines where its inputs come from and what reads it. Clicking a step opens its details in
  a bottom sheet (`?step=`): status, reason, latest attempt, recent runs, what it reads and what
  reads it, its definition, and the actions (logs, cancel, rerun, block / unblock,
  the step's runs).
- **Run** (`/pipeline/:id/run/:n?step=&attempt=`): a one-line header (run, status, duration,
  rerun of / superseded by / held at, Cancel while running, Trigger again), a status card (version,
  trigger, started, critical path, input; trigger and input open a panel with the event and its
  timeline, or the full input), the changes since the newest older run that succeeded, then the
  pinned version's graph coloured by step status. The selected step's sheet holds its attempts with
  typed input and output, files, live logs and external inputs, and cancel / rerun step (with or
  without continue); a click on nothing closes it.
- **CI** (`/pipeline/:id/ci`, `/ci/queue`, `/ci/closed`): open pull requests with their latest CI
  run (status, step strip, stale, conflict, queue place), the merge queue (queued, testing, merged,
  removed), closed and merged pull requests.
- **CI run** (`/pipeline/:id/ci/run/:n`): what it tests (a pull request, or a queue batch with the
  main it was rebased onto), its commit and GitHub check, the main it ran on when stale, the graph
  by step status (a reused step links the run it came from), the pull request's runs; Cancel,
  Rerun. A step's log: `/pipeline/:id/ci/run/:n/step/:name/logs`.
- **Step log** (`/pipeline/:id/run/:n/step/:name/logs`) and **step runs**
  (`/pipeline/:id/step/:name/runs`): one attempt's log full height; a step's attempts across runs.
- **Critical path** (`/pipeline/:id/critical-path`): each step's share of a range of runs' time.
- **Reliability** (`/pipeline/:id/reliability`): each step's failure rate and flakiness over the
  latest runs, and the share of runs that failed for no real reason.
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
