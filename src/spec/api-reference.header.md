# API Reference

[← Index](index.md)

The HTTP API that `pl` and the web UI use. This page is generated from the API contract
(`src/spec/*.tsp`, compiled to OpenAPI) by `src/spec/scripts/api-reference.ts`; an instance serves
its live OpenAPI document at `/openapi/v1.json` to a logged-in client. Prefer `pl` for everyday use.

## Conventions

- **Auth.** Every endpoint needs a bearer token (the instance's OIDC provider; `pl login`) except
  `/api/health`, `/api/ready`, `/api/config/frontend`, the webhook receivers (verified with an HMAC
  signature or the trigger's secret), `/download`, the install scripts and `/docs`.
  `Auth__AnonymousReads=true` opens the `GET` endpoints too, except listing secrets and listing or
  downloading step files. With `Auth__Allow__*` set, a valid token of anyone outside the allow-list
  gets 403.
- **Addressing.** Pipelines by id (`{pipelineId}`), runs by number, steps by name:
  `/api/pipelines/{pipelineId}/runs/{number}/steps/{step}/…`. Only cross-pipeline views are
  top-level (`/api/attempts`).
- **Errors.** Any non-2xx answer except 401 and 403 is `application/json`, an
  [ApiProblem](#apiproblem): `{type, title, status, detail, errors: [{code, message, field?}],
  traceId}`. The codes are `not-found` (404), `conflict` (409), `invalid` (400), `unavailable` (503,
  with `Retry-After`) and `internal` (500).

## Live events

`GET /api/events?pipeline=<id>` (omit `pipeline` for every pipeline) is a `text/event-stream` of
change notifications. Each event is a small "something changed" notice in camelCase JSON (its
`type` repeats the event name); the client re-fetches the view it affects.

| Event | Data | Fired when |
|-------|------|------------|
| `run` | `{pipelineId, run, status}` | A run is created, changes (status, held, resumed) or is deleted (`status: "Deleted"`) |
| `attempt` | `{pipelineId, run, step, attempt, status}` | An attempt is created or changes status |
| `version` | `{pipelineId, version, change}` | `added` (new version), `active` (switched, or a switch finished), `draining` (a switch to `version` started), `deleted` |
| `blocked` | `{pipelineId, blockedSteps}` | A step is blocked or unblocked (the full blocked list) |
| `binding` | `{pipelineId, state}` | The binding changes: `reconciling`, then `Success` or `Error`; also `removed` |
| `pipeline` | `{pipelineId, change}` | `added`, `updated`, `removed` |
| `config` | `{pipelineId, snapshot}` | A config snapshot is saved (`pl config set`, rollback) |
| `pending` | `{pipelineId, version}` | A version starts waiting for valid config (`version`), or none waits any more (`null`) |
| `queued` | `{pipelineId, queued}` | Run starts are queued during a switch with `--cancel-running`, or the queue empties |
| `secrets` | `{pipelineId}` | A pipeline secret is set, deleted or copied in (names only, never values) |
| `poll` | `{pipelineId}` | A poll trigger saw a new value |
| `hello` | `{lastEventId}` | First event of every connection |
| `resync` | `{reason}` | The client reconnected with a stale `Last-Event-ID` (or across a restart): refetch everything |
| `ping` | `{}` | Keep-alive every 15 s while idle |

Ids are `<boot>-<seq>`; there is no replay. A client that falls 256 events behind is disconnected
(its `EventSource` reconnects and gets `resync`). Streams end when the service shuts down.

## Outside the API

| Method | Path | What it is |
|--------|------|------------|
| GET, HEAD | `/download/{asset}` | The `pl` binary (`pl-linux-x64`, `pl-win-x64.exe`) this instance serves; 404 until it has one |
| GET | `/install.sh`, `/install.ps1` | The `pl` install scripts, defaulting to this instance |
| GET | `/docs/<page>.md`, `/docs/llms.txt` | These docs, as raw Markdown |
| GET | `/api/pipelines/{pipelineId}/runs/{number}/steps/{step}/files/{path}` | One stored file of an attempt (`?attempt=`, default the latest successful one); a directory `dir/` downloads as `dir.tar`. Needs a token |
| POST | `/api/hooks/{pipelineId}/{trigger}` | A delivery for the pipeline's `onWebhook` trigger: anonymous, verified with the trigger's secret, answers 202 (the delivery is mapped to a run input in the background), 401, 404, 413, 429 or 503 |
| POST | `/api/hooks/inputs/{run}/{step}/{input}/{mac}` | Another system answering an external input of a waiting step: anonymous, verified by the MAC in the URL the step was given |
| POST | `/api/webhooks/binding/{bindingId}/github` | The binding's GitHub push webhook, verified with its HMAC secret |
| GET | `/openapi/v1.json` | This instance's live OpenAPI document: every route it maps, these included |
