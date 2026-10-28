# API Reference

[← Index](index.md)

The HTTP API that `pl` and the web UI use. This page is generated from the OpenAPI document
(`api.json` in the repo; an instance serves it at `/openapi/v1.json` to a logged-in client) by
`tools/api-reference.cs`. Prefer `pl` for everyday use.

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
- **Errors.** Any non-2xx answer except 401 and 403 is `application/problem+json`, an
  [ApiProblem](#apiproblem): `{type, title, status, detail, errors: [{code, message, field?}],
  traceId}`. The codes are `not-found` (404), `conflict` (409), `invalid` (400), `unavailable` (503,
  with `Retry-After`) and `internal` (500).

## Live events

`GET /api/events?pipeline=<id>` (omit `pipeline` for every pipeline) is a `text/event-stream` of
change notifications. Each event is a small "something changed" notice in camelCase JSON; the
client re-fetches the view it affects.

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
| `hello` | `{lastEventId}` | First event of every connection; carries `retry: 5000` |
| `resync` | `{reason}` | The client reconnected with a stale `Last-Event-ID` (or across a restart): refetch everything |
| `ping` | (empty) | Keep-alive every 15 s while idle |

Ids are `<boot>-<seq>`; there is no replay. A client that falls 256 events behind is disconnected
(its `EventSource` reconnects and gets `resync`). Streams end when the service shuts down.

## Outside the API

| Method | Path | What it is |
|--------|------|------------|
| GET, HEAD | `/download/{asset}` | The `pl` binary (`pl-linux-x64`, `pl-win-x64.exe`) this instance serves; 404 until it has one |
| GET | `/install.sh`, `/install.ps1` | The `pl` install scripts, defaulting to this instance |
| GET | `/docs/<page>.md`, `/docs/llms.txt` | These docs, as raw Markdown |

## Endpoints

### Pipelines

#### List pipelines

`GET /api/pipelines`

**Returns:** `200` [Pipeline](#pipeline)[]

#### Delete a pipeline

`DELETE /api/pipelines/{pipelineId}`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200`

#### Get a pipeline

`GET /api/pipelines/{pipelineId}`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` [Pipeline](#pipeline)

#### Get a pipeline's health

`GET /api/pipelines/{pipelineId}/health`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` [PipelineHealth](#pipelinehealth)

### Bindings

#### Create a pipeline bound to a repo

`POST /api/pipelines`

**Body** (`application/json`): [CreatePipeline](#createpipeline)

**Returns:** `201` [PipelineBinding](#pipelinebinding)

#### Get the binding

`GET /api/pipelines/{pipelineId}/binding`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` [PipelineBinding](#pipelinebinding)

#### Update the binding

`PATCH /api/pipelines/{pipelineId}/binding`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): [UpdateBinding](#updatebinding)

**Returns:** `200` [PipelineBinding](#pipelinebinding)

#### Reconcile now

`POST /api/pipelines/{pipelineId}/binding/reconcile`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `wait` | query | boolean |  |
| `rebuild` | query | boolean |  |

**Returns:** `200`

#### Move the binding to another branch

`PUT /api/pipelines/{pipelineId}/binding/branch`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): [SetBindingBranch](#setbindingbranch)

**Returns:** `200` [PipelineBinding](#pipelinebinding)

#### Turn the binding's polling on or off

`PUT /api/pipelines/{pipelineId}/binding/polling`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): [SetBindingPolling](#setbindingpolling)

**Returns:** `200` [PipelineBinding](#pipelinebinding)

#### Get the binding's reconcile status

`GET /api/pipelines/{pipelineId}/binding/status`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` [PipelineBindingStatus](#pipelinebindingstatus)

#### GitHub push webhook

`POST /api/webhooks/binding/{bindingId}/github`

Receives the binding's GitHub push webhook. Anonymous, verified with the webhook's HMAC signature.

| Parameter | In | Type | Required |
|---|---|---|---|
| `bindingId` | path | string | yes |

**Returns:** `200`

### Versions

#### Get a version's effective pipeline

`GET /api/pipelines/{pipelineId}/versions/{number}/effective`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `snapshot` | query | integer |  |

**Returns:** `200` [EffectivePipeline](#effectivepipeline)

#### Get a version

`GET /api/pipelines/{pipelineId}/versions/{number}`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |

**Returns:** `200` [PipelineVersion](#pipelineversion)

#### List versions

`GET /api/pipelines/{pipelineId}/versions`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` [PipelineVersions](#pipelineversions)

#### Switch the active version

`POST /api/pipelines/{pipelineId}/switch`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): [SwitchVersion](#switchversion)

**Returns:** `200` [PipelineActivation](#pipelineactivation)

#### Validate a manifest

`POST /api/manifests/validate`

Runs the reconcile checks on a manifest (`pl dev validate`). Always 200, with `valid`, `manifestHash`, `errors` and `warnings`.

**Body** (`application/json`): any

**Returns:** `200` [ManifestValidationResponse](#manifestvalidationresponse)

### Config

#### Check the config

`GET /api/pipelines/{pipelineId}/config/check`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `version` | query | integer |  |

**Returns:** `200` [ConfigCheck](#configcheck)

#### Get the config document

`GET /api/pipelines/{pipelineId}/config`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `snapshot` | query | integer |  |

**Returns:** `200` [ConfigDocument](#configdocument)

#### Save the config document

`PUT /api/pipelines/{pipelineId}/config`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): [SetConfig](#setconfig)

**Returns:** `200` [ConfigSaveResult](#configsaveresult)

#### List config snapshots

`GET /api/pipelines/{pipelineId}/config/history`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` [ConfigSnapshotSummary](#configsnapshotsummary)[]

#### Roll back the config

`POST /api/pipelines/{pipelineId}/config/rollback`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): [RollbackConfig](#rollbackconfig)

**Returns:** `200` [ConfigSaveResult](#configsaveresult)

### Secrets

#### Copy secrets from another pipeline

`POST /api/pipelines/{pipelineId}/secrets/copy`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): [CopySecrets](#copysecrets)

**Returns:** `200` [CopiedSecrets](#copiedsecrets)

#### Delete a secret

`DELETE /api/pipelines/{pipelineId}/secrets/{name}`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `name` | path | string | yes |
| `force` | query | boolean |  |

**Returns:** `200`

#### Set a secret

`PUT /api/pipelines/{pipelineId}/secrets/{name}`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `name` | path | string | yes |

**Body** (`application/json`): [SetSecret](#setsecret)

**Returns:** `200`

#### List secrets

`GET /api/pipelines/{pipelineId}/secrets`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` [PipelineSecret](#pipelinesecret)[]

### Runs

#### List a run's attempts

`GET /api/pipelines/{pipelineId}/runs/{number}/attempts`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |

**Returns:** `200` [Attempt](#attempt)[]

#### Cancel a run

`POST /api/pipelines/{pipelineId}/runs/{number}/cancel`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |

**Returns:** `200` [Run](#run)

#### Continue a run

`POST /api/pipelines/{pipelineId}/runs/{number}/continue`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |

**Body** (`application/json`): [ContinueRun](#continuerun)

**Returns:** `200` [Run](#run)

#### Get the critical path of a range of runs

`GET /api/pipelines/{pipelineId}/runs/critical-path`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `from` | query | integer |  |
| `to` | query | integer |  |
| `includeUnsuccessful` | query | boolean |  |
| `sinceVersion` | query | integer |  |

**Returns:** `200` [CriticalPathReport](#criticalpathreport)

#### Get a run

`GET /api/pipelines/{pipelineId}/runs/{number}`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |

**Returns:** `200` [Run](#run)

#### List runs

`GET /api/pipelines/{pipelineId}/runs`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `offset` | query | integer |  |
| `limit` | query | integer |  |

**Returns:** `200` [RunList](#runlist)

#### Start a run

`POST /api/pipelines/{pipelineId}/runs`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Body** (`application/json`): object

**Returns:** `200` [RunStart](#runstart)

#### Rerun a run

`POST /api/pipelines/{pipelineId}/runs/{number}/rerun`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |

**Returns:** `200` [RunStart](#runstart)

#### Answer a callback input

`POST /api/hooks/inputs/{runId}/{step}/{input}/{mac}`

Another system answers a step's external input with `from: [callback()]`. The URL comes from `ctx.inputUrl(step, input)` and is the credential: anonymous, valid while the step waits for that input. The body is the value as JSON, or `{"$decline": "reason"}` to fail the step without running it. Answers 202.

| Parameter | In | Type | Required |
|---|---|---|---|
| `runId` | path | string | yes |
| `step` | path | string | yes |
| `input` | path | string | yes |
| `mac` | path | string | yes |

**Returns:** `200`

### Steps

#### Answer a step's external input

`POST /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/input`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Body** (`application/json`): [AnswerInput](#answerinput)

**Returns:** `200` [Attempt](#attempt)

#### List a step's attempts in a run

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/attempts`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Returns:** `200` [Attempt](#attempt)[]

#### Cancel a step

`POST /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/cancel`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Returns:** `200` [Attempt](#attempt)

#### Decline a step's external input

`POST /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/input/decline`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Body** (`application/json`): [DeclineInput](#declineinput)

**Returns:** `200` [Attempt](#attempt)

#### List a step's files

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/files`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200` [StepFiles](#stepfiles)

#### Get a step's log

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/logs`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200` string

#### Get a step's output

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/output`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200` [StepOutput](#stepoutput)

#### Rerun a step

`POST /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/rerun`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Body** (`application/json`): [RerunStep](#rerunstep)

**Returns:** `200` [Attempt](#attempt)

#### Block a step

`POST /api/pipelines/{pipelineId}/steps/{step}/block`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `step` | path | string | yes |

**Returns:** `200` string[]

#### List blocked steps

`GET /api/pipelines/{pipelineId}/blocked-steps`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |

**Returns:** `200` string[]

#### Get a step's history

`GET /api/pipelines/{pipelineId}/steps/{step}/history`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `step` | path | string | yes |
| `limit` | query | integer |  |

**Returns:** `200` [StepHistoryItem](#stephistoryitem)[]

#### Unblock a step

`POST /api/pipelines/{pipelineId}/steps/{step}/unblock`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string (uuid) | yes |
| `step` | path | string | yes |

**Returns:** `200` string[]

#### Download a step file

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/files/{path}`

One stored file; a directory `dir/` downloads as `dir.tar`.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `path` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200`

#### Stream a step's log

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/logs/stream`

The attempt's log as server-sent events (`attempt`, default the latest): `log` events `{text, reset}`, then `end` `{status}` once the attempt is finished.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200` string (`text/event-stream`)

### Attempts

#### List running attempts

`GET /api/attempts`

| Parameter | In | Type | Required |
|---|---|---|---|
| `status` | query | string |  |

**Returns:** `200` [ActiveAttempt](#activeattempt)[]

### Triggers

#### Deliver a webhook trigger

`POST /api/hooks/{pipelineId}/{trigger}`

A delivery for the pipeline's `onWebhook` trigger `trigger`. Anonymous, verified with the trigger's secret. Answers 202; the delivery is mapped to a run input in the background.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `trigger` | path | string | yes |

**Returns:** `200`

### Events

#### Stream change events

`GET /api/events`

Server-sent change notifications for one pipeline (`pipeline`) or all; see Live events above.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipeline` | query | string |  |
| `Last-Event-ID` | header | string |  |

**Returns:** `200` string (`text/event-stream`)

### Instance

#### Get the login settings

`GET /api/config/frontend`

**Returns:** `200` [FrontendConfig](#frontendconfig)

#### Liveness

`GET /api/health`

**Returns:** `200` [HealthResponse](#healthresponse)

#### Readiness

`GET /api/ready`

**Returns:** `200`

## Schemas

### ActiveAttempt

| Field | Type | |
|---|---|---|
| `pipelineId` | string (uuid) | required |
| `pipelineName` | string | required |
| `run` | integer | required |
| `step` | string | required |
| `attempt` | integer | required |
| `status` | string | required |
| `createdAt` | string (date-time) | required |
| `startedAt` | string (date-time)? | required |

### AnswerInput

| Field | Type | |
|---|---|---|
| `value` | any | required |
| `name` | string? |  |

### ApiProblem

| Field | Type | |
|---|---|---|
| `type` | string | required |
| `title` | string | required |
| `status` | integer | required |
| `detail` | string? | required |
| `errors` | [ApiProblemError](#apiproblemerror)[] | required |
| `traceId` | string? | required |

### ApiProblemError

| Field | Type | |
|---|---|---|
| `code` | `"not-found"` \| `"conflict"` \| `"invalid"` \| `"unavailable"` \| `"internal"` | required |
| `message` | string | required |
| `field` | string? |  |

### Attempt

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `pipelineId` | string (uuid) | required |
| `runId` | string (uuid) | required |
| `runNumber` | integer | required |
| `stepName` | string | required |
| `number` | integer | required |
| `status` | [AttemptStatus](#attemptstatus) | required |
| `createdAt` | string (date-time) | required |
| `inputHash` | string? |  |
| `output` | [AttemptOutput](#attemptoutput)? |  |
| `input` | string? |  |
| `timings` | [AttemptTimings](#attempttimings)? |  |
| `answers` | map of [ExternalAnswer](#externalanswer)? |  |

### AttemptOutput

| Field | Type | |
|---|---|---|
| `key` | string | required |
| `json` | string | required |

### AttemptPhases

| Field | Type | |
|---|---|---|
| `queued` | number | required |
| `podStart` | number? | required |
| `fetch` | number? | required |
| `image` | number? | required |
| `active` | number? | required |
| `collect` | number? | required |
| `notice` | number? |  |

### AttemptStatus

| Field | Type | |
|---|---|---|
| `type` | string | required |

### AttemptStatusAttemptStatusCancelled

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time)? | required |
| `cancelledAt` | string (date-time) | required |

### AttemptStatusAttemptStatusFailed

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time) | required |
| `finishedAt` | string (date-time) | required |
| `reason` | string? |  |

### AttemptStatusAttemptStatusInProgress

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time) | required |

### AttemptStatusAttemptStatusObsolete

| Field | Type | |
|---|---|---|
| `supersedingAttemptId` | string (uuid) | required |

### AttemptStatusAttemptStatusScheduled

| Field | Type | |
|---|---|---|
| `submissionAttempts` | integer? |  |

### AttemptStatusAttemptStatusSkipped

| Field | Type | |
|---|---|---|
| `reason` | string | required |
| `startedAt` | string (date-time)? | required |
| `finishedAt` | string (date-time) | required |

### AttemptStatusAttemptStatusSucceeded

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time) | required |
| `finishedAt` | string (date-time) | required |

### AttemptStatusAttemptStatusWaiting

| Field | Type | |
|---|---|---|
| `since` | string (date-time) | required |

### AttemptTimings

| Field | Type | |
|---|---|---|
| `podCreatedAt` | string (date-time)? | required |
| `initStartedAt` | string (date-time)? | required |
| `initFinishedAt` | string (date-time)? | required |
| `stepStartedAt` | string (date-time)? | required |
| `stepFinishedAt` | string (date-time)? | required |
| `observedAt` | string (date-time)? |  |

### ChangedDefault

| Field | Type | |
|---|---|---|
| `path` | string | required |
| `from` | string | required |
| `to` | string | required |

### ConfigCheck

| Field | Type | |
|---|---|---|
| `version` | integer | required |
| `valid` | boolean | required |
| `satisfied` | boolean | required |
| `problems` | string[] | required |
| `referencedSecrets` | string[] | required |
| `missingSecrets` | string[] | required |
| `changedDefaults` | [ChangedDefault](#changeddefault)[] | required |
| `effective` | string? | required |
| `unmet` | string[] | required |

### ConfigDocument

| Field | Type | |
|---|---|---|
| `snapshot` | [ConfigSnapshotSummary](#configsnapshotsummary)? | required |
| `yaml` | string | required |

### ConfigSaveResult

| Field | Type | |
|---|---|---|
| `snapshot` | [ConfigSnapshotSummary](#configsnapshotsummary) | required |
| `active` | [ConfigCheck](#configcheck)? | required |
| `pending` | [ConfigCheck](#configcheck)? | required |
| `activatedVersion` | integer? | required |

### ConfigSnapshotSummary

| Field | Type | |
|---|---|---|
| `number` | integer | required |
| `author` | string | required |
| `createdAt` | string (date-time) | required |
| `restoredFrom` | integer? | required |

### ContinueRun

| Field | Type | |
|---|---|---|
| `input` | object? |  |

### CopiedSecrets

| Field | Type | |
|---|---|---|
| `copied` | string[] | required |

### CopySecrets

| Field | Type | |
|---|---|---|
| `sourcePipelineId` | string (uuid) | required |
| `names` | string[]? |  |
| `targetName` | string? |  |
| `overwrite` | boolean? |  |

### CreatePipeline

| Field | Type | |
|---|---|---|
| `repo` | string | required |
| `branch` | string? |  |
| `path` | string? |  |
| `credentialsSecret` | string? |  |

### CriticalPathLead

| Field | Type | |
|---|---|---|
| `seconds` | number | required |
| `via` | string | required |
| `detect` | number? | required |
| `check` | number? | required |
| `map` | number? | required |
| `start` | number? | required |

### CriticalPathReport

| Field | Type | |
|---|---|---|
| `from` | integer | required |
| `to` | integer | required |
| `runs` | [CriticalPathRun](#criticalpathrun)[] | required |
| `steps` | [CriticalPathStep](#criticalpathstep)[] | required |
| `skipped` | [CriticalPathSkip](#criticalpathskip)[] | required |
| `totalSeconds` | number | required |
| `averageSeconds` | number | required |

### CriticalPathRun

| Field | Type | |
|---|---|---|
| `number` | integer | required |
| `status` | `"Running"` \| `"Succeeded"` \| `"Failed"` \| `"Cancelled"` \| `"Superseded"` | required |
| `versionNumber` | integer | required |
| `versionStamp` | string | required |
| `seconds` | number | required |
| `idleSeconds` | number | required |
| `path` | [CriticalPathStage](#criticalpathstage)[] | required |
| `notes` | string[] | required |
| `activeSeconds` | number? |  |
| `lead` | [CriticalPathLead](#criticalpathlead)? |  |

### CriticalPathSkip

| Field | Type | |
|---|---|---|
| `number` | integer | required |
| `reason` | string | required |

### CriticalPathStage

| Field | Type | |
|---|---|---|
| `stage` | integer | required |
| `step` | string | required |
| `attempts` | integer | required |
| `seconds` | number | required |
| `waitSeconds` | number | required |
| `activeSeconds` | number? |  |

### CriticalPathStep

| Field | Type | |
|---|---|---|
| `step` | string | required |
| `runs` | integer | required |
| `seconds` | number | required |
| `share` | number | required |
| `averageSeconds` | number | required |
| `waitSeconds` | number | required |
| `runNumbers` | integer[] | required |
| `overheadSeconds` | number? |  |

### DeclineInput

| Field | Type | |
|---|---|---|
| `comment` | string? |  |

### EffectivePipeline

| Field | Type | |
|---|---|---|
| `version` | integer | required |
| `configSnapshot` | integer? | required |
| `selected` | map of string | required |
| `manifest` | string | required |
| `cases` | map of string[] | required |

### ExternalAnswer

| Field | Type | |
|---|---|---|
| `value` | string | required |
| `answeredBy` | string | required |
| `answeredAt` | string (date-time) | required |
| `source` | string? |  |

### FrontendConfig

| Field | Type | |
|---|---|---|
| `oidcAuthority` | string? |  |
| `oidcClientId` | string? |  |
| `oidcScopes` | string | required |

### HealthResponse

| Field | Type | |
|---|---|---|
| `status` | string | required |
| `image` | string? |  |
| `version` | string? |  |

### ManifestIssueView

| Field | Type | |
|---|---|---|
| `code` | string | required |
| `severity` | string | required |
| `message` | string | required |
| `path` | string | required |
| `step` | string? | required |

### ManifestValidationResponse

| Field | Type | |
|---|---|---|
| `valid` | boolean | required |
| `manifestHash` | string? | required |
| `errors` | [ManifestIssueView](#manifestissueview)[] | required |
| `warnings` | [ManifestIssueView](#manifestissueview)[] | required |

### PendingVersionInfo

| Field | Type | |
|---|---|---|
| `version` | integer | required |
| `since` | string (date-time) | required |
| `held` | boolean | required |
| `reasons` | string[] | required |
| `changedDefaults` | [ChangedDefault](#changeddefault)[] | required |

### Pipeline

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `name` | string | required |
| `description` | string? | required |
| `repo` | string? | required |
| `health` | [PipelineHealth](#pipelinehealth) | required |
| `stages` | [PipelineStageSummary](#pipelinestagesummary)[] | required |

### PipelineActivation

| Field | Type | |
|---|---|---|
| `type` | string | required |

### PipelineActivationPipelineActivationActive

| Field | Type | |
|---|---|---|
| `versionId` | string (uuid) | required |
| `versionNumber` | integer | required |
| `since` | string (date-time) | required |

### PipelineActivationPipelineActivationDraining

| Field | Type | |
|---|---|---|
| `fromVersionId` | string (uuid) | required |
| `fromVersionNumber` | integer | required |
| `toVersionId` | string (uuid) | required |
| `toVersionNumber` | integer | required |
| `cancelRunning` | boolean | required |
| `since` | string (date-time) | required |

### PipelineBinding

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `pipelineId` | string (uuid) | required |
| `repo` | string | required |
| `branch` | string | required |
| `path` | string | required |
| `credentialsSecret` | string? | required |
| `lastPushSha` | string? | required |
| `lastReconciledSha` | string? | required |
| `status` | [ReconcileStatus](#reconcilestatus) | required |
| `createdAt` | string (date-time) | required |
| `polls` | boolean | required |

### PipelineBindingStatus

| Field | Type | |
|---|---|---|
| `pipelineId` | string (uuid) | required |
| `repo` | string | required |
| `branch` | string | required |
| `path` | string | required |
| `lastPushSha` | string? | required |
| `lastReconciledSha` | string? | required |
| `result` | `"NeverRun"` \| `"Success"` \| `"Error"` | required |
| `lastSyncTime` | string (date-time)? | required |
| `problems` | string[] | required |
| `warnings` | string[] | required |
| `reconcileSha` | string? | required |
| `reconcileInProgress` | boolean | required |
| `reconciledVersion` | integer? | required |
| `activation` | [PipelineActivation](#pipelineactivation)? | required |
| `pending` | [PendingVersionInfo](#pendingversioninfo)? |  |

### PipelineHealth

| Field | Type | |
|---|---|---|
| `status` | `"Healthy"` \| `"Running (Healthy)"` \| `"Running (Unhealthy)"` \| `"Unhealthy"` \| `"Idle"` | required |
| `lastRun` | [RunSummary](#runsummary)? | required |
| `lastSettledRun` | [RunSummary](#runsummary)? | required |
| `runningRuns` | integer | required |
| `activation` | [PipelineActivation](#pipelineactivation)? | required |
| `draining` | boolean | required |
| `blockedSteps` | string[] | required |
| `heldRuns` | [RunSummary](#runsummary)[] | required |
| `pending` | [PendingVersionInfo](#pendingversioninfo)? |  |

### PipelineSecret

| Field | Type | |
|---|---|---|
| `name` | string | required |
| `description` | string? | required |
| `declaredBy` | string[] | required |
| `isSet` | boolean? | required |
| `needed` | boolean? |  |

### PipelineStageSummary

| Field | Type | |
|---|---|---|
| `when` | [RunStageCondition](#runstagecondition)[] | required |
| `steps` | [PipelineStepSummary](#pipelinestepsummary)[] | required |

### PipelineStepSummary

| Field | Type | |
|---|---|---|
| `name` | string | required |
| `status` | string? | required |
| `runNumber` | integer? | required |
| `at` | string (date-time)? | required |

### PipelineVersion

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `number` | integer | required |
| `manifestHash` | string | required |
| `commitSha` | string? | required |
| `stepVersions` | map of integer | required |
| `modelVersions` | map of integer | required |
| `createdAt` | string (date-time) | required |
| `pipelineId` | string (uuid) | required |
| `manifest` | string | required |

### PipelineVersionSummary

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `number` | integer | required |
| `manifestHash` | string | required |
| `commitSha` | string? | required |
| `stepVersions` | map of integer | required |
| `modelVersions` | map of integer | required |
| `createdAt` | string (date-time) | required |

### PipelineVersions

| Field | Type | |
|---|---|---|
| `activation` | [PipelineActivation](#pipelineactivation)? | required |
| `versions` | [PipelineVersionSummary](#pipelineversionsummary)[] | required |

### ReconcileStatus

| Field | Type | |
|---|---|---|
| `result` | `"NeverRun"` \| `"Success"` \| `"Error"` | required |
| `lastSyncTime` | string (date-time)? | required |
| `problems` | string[] | required |
| `warnings` | string[] | required |
| `sha` | string? | required |
| `inProgress` | boolean | required |
| `versionNumber` | integer? | required |

### RerunStep

| Field | Type | |
|---|---|---|
| `continue` | boolean? |  |

### RollbackConfig

| Field | Type | |
|---|---|---|
| `snapshot` | integer | required |

### Run

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `number` | integer | required |
| `status` | `"Running"` \| `"Succeeded"` \| `"Failed"` \| `"Cancelled"` \| `"Superseded"` | required |
| `triggerKind` | `"Push"` \| `"Poll"` \| `"Manual"` \| `"Webhook"` | required |
| `versionNumber` | integer | required |
| `versionStamp` | string | required |
| `createdAt` | string (date-time) | required |
| `finishedAt` | string (date-time)? | required |
| `heldAt` | string? | required |
| `supersededByNumber` | integer? | required |
| `rerunOfNumber` | integer? | required |
| `configSnapshot` | integer? |  |
| `versionId` | string (uuid) | required |
| `commitSha` | string? | required |
| `input` | object | required |
| `continuable` | boolean | required |
| `stages` | [RunStage](#runstage)[] | required |
| `config` | string? |  |

### RunList

| Field | Type | |
|---|---|---|
| `items` | [RunSummary](#runsummary)[] | required |
| `offset` | integer | required |
| `limit` | integer | required |
| `total` | integer | required |

### RunStage

| Field | Type | |
|---|---|---|
| `index` | integer | required |
| `when` | [RunStageCondition](#runstagecondition)[] | required |
| `steps` | [RunStep](#runstep)[] | required |

### RunStageCondition

| Field | Type | |
|---|---|---|
| `field` | string | required |
| `values` | any[] | required |

### RunStart

| Field | Type | |
|---|---|---|
| `outcome` | `"started"` \| `"deduplicated"` \| `"queued"` | required |
| `run` | [Run](#run)? | required |

### RunStep

| Field | Type | |
|---|---|---|
| `name` | string | required |
| `stepVersion` | integer? | required |
| `status` | string | required |
| `reason` | string? | required |
| `attemptCount` | integer | required |
| `latestAttempt` | integer? | required |
| `succeededAttempt` | integer? | required |
| `startedAt` | string (date-time)? | required |
| `finishedAt` | string (date-time)? | required |
| `blocked` | boolean | required |
| `phases` | [AttemptPhases](#attemptphases)? |  |
| `waitingFor` | string[]? |  |

### RunSummary

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `number` | integer | required |
| `status` | `"Running"` \| `"Succeeded"` \| `"Failed"` \| `"Cancelled"` \| `"Superseded"` | required |
| `triggerKind` | `"Push"` \| `"Poll"` \| `"Manual"` \| `"Webhook"` | required |
| `versionNumber` | integer | required |
| `versionStamp` | string | required |
| `createdAt` | string (date-time) | required |
| `finishedAt` | string (date-time)? | required |
| `heldAt` | string? | required |
| `supersededByNumber` | integer? | required |
| `rerunOfNumber` | integer? | required |
| `configSnapshot` | integer? |  |

### SetBindingBranch

| Field | Type | |
|---|---|---|
| `branch` | string | required |

### SetBindingPolling

| Field | Type | |
|---|---|---|
| `polls` | boolean | required |

### SetConfig

| Field | Type | |
|---|---|---|
| `yaml` | string | required |

### SetSecret

| Field | Type | |
|---|---|---|
| `value` | string | required |

### StepFile

| Field | Type | |
|---|---|---|
| `path` | string | required |
| `size` | integer | required |
| `lastModified` | string (date-time)? | required |

### StepFiles

| Field | Type | |
|---|---|---|
| `run` | integer | required |
| `step` | string | required |
| `attempt` | integer | required |
| `files` | [StepFile](#stepfile)[] | required |

### StepHistoryItem

| Field | Type | |
|---|---|---|
| `runNumber` | integer | required |
| `runStatus` | `"Running"` \| `"Succeeded"` \| `"Failed"` \| `"Cancelled"` \| `"Superseded"` | required |
| `versionNumber` | integer | required |
| `versionStamp` | string | required |
| `attemptId` | string (uuid) | required |
| `attemptNumber` | integer | required |
| `status` | string | required |
| `reason` | string? | required |
| `createdAt` | string (date-time) | required |
| `startedAt` | string (date-time)? | required |
| `finishedAt` | string (date-time)? | required |
| `stepVersion` | integer? | required |
| `stepVersionChanged` | boolean | required |
| `phases` | [AttemptPhases](#attemptphases)? |  |

### StepOutput

| Field | Type | |
|---|---|---|
| `run` | integer | required |
| `step` | string | required |
| `attempt` | integer | required |
| `status` | string | required |
| `output` | any? | required |
| `outputKey` | string? | required |

### SwitchVersion

| Field | Type | |
|---|---|---|
| `version` | integer | required |
| `cancelRunning` | boolean? |  |

### UpdateBinding

| Field | Type | |
|---|---|---|
| `credentialsSecret` | string? | required |
