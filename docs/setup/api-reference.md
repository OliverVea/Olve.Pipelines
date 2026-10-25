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
  downloading step files.
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
| `version` | `{pipelineId, version, change}` | `added` (new version), `active` (switched, or a switch finished), `draining` (a switch to `version` started) |
| `blocked` | `{pipelineId, blockedSteps}` | A step is blocked or unblocked (the full blocked list) |
| `binding` | `{pipelineId, state}` | The binding changes: `reconciling`, then `Success` or `Error`; also `removed` |
| `pipeline` | `{pipelineId, change}` | `added`, `updated`, `removed` |
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

#### Get a pipeline

`GET /api/pipelines/{id}`

| Parameter | In | Type | Required |
|---|---|---|---|
| `id` | path | string | yes |

**Returns:** `200` [Pipeline](#pipeline)

#### Delete a pipeline

`DELETE /api/pipelines/{id}`

Deletes the pipeline with its runs, versions, binding and secrets.

| Parameter | In | Type | Required |
|---|---|---|---|
| `id` | path | string | yes |

**Returns:** `200`

#### List pipelines

`GET /api/pipelines`

**Returns:** `200` [Pipeline](#pipeline)[]

#### List pipeline cards

`GET /api/pipelines/summary`

One card per pipeline for the list page: id, name, description, repo and health.

**Returns:** `200` [PipelineSummary](#pipelinesummary)[]

#### Get a pipeline's health

`GET /api/pipelines/{pipelineId}/health`

Run-based health: the last run, running and held runs, blocked steps and the activation.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200` [PipelineHealth](#pipelinehealth)

### Bindings

#### Create a pipeline bound to a repo

`POST /api/pipelines/with-repo`

The only way to create a pipeline. The binding watches `path` (default `.pipelines`) on `branch` of `repo`; `credentialsSecret` names the pipeline-secret key holding a GitHub token.

**Body** (`application/json`): [CreatePipelineWithRepoRequest](#createpipelinewithreporequest)

**Returns:** `200` [PipelineConfigBinding](#pipelineconfigbinding)

#### Reconcile now

`POST /api/pipelines/{pipelineId}/binding/reconcile`

Queues a reconcile of the bound config and returns at once; progress shows in the binding status. Also retries a failed commit.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200`

#### Update the binding

`PATCH /api/pipelines/{pipelineId}/binding`

Sets or clears the credentials secret key.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Body** (`application/json`): [UpdateBindingRequest](#updatebindingrequest)

**Returns:** `200` [PipelineConfigBinding](#pipelineconfigbinding)

#### Get the binding

`GET /api/pipelines/{pipelineId}/binding`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200` [PipelineConfigBinding](#pipelineconfigbinding)

#### GitHub push webhook

`POST /api/webhooks/binding/{bindingId}/github`

Receives the binding's GitHub push webhook. Anonymous, verified with the webhook's HMAC signature.

| Parameter | In | Type | Required |
|---|---|---|---|
| `bindingId` | path | string | yes |

**Returns:** `200`

#### Get the binding's reconcile status

`GET /api/pipelines/{pipelineId}/binding/status`

The last reconcile's result and commit, whether one is in progress, the resulting and the active version, problems and warnings.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200` [PipelineBindingStatus](#pipelinebindingstatus)

### Versions

#### Validate a manifest

`POST /api/manifests/validate`

Runs the reconcile checks on a manifest (`pl dev validate`). Always 200, with `valid`, `manifestHash`, `errors` and `warnings`.

**Body** (`application/json`): any

**Returns:** `200` [ManifestValidationResponse](#manifestvalidationresponse)

#### Switch the active version

`POST /api/pipelines/{pipelineId}/switch`

New runs start on `version`. `cancelRunning` cancels the other versions' runs and drains first. 409, with the reasons, when the config document or a secret it references doesn't satisfy that version.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Body** (`application/json`): [SwitchVersionRequest](#switchversionrequest)

**Returns:** `200` [PipelineActivation](#pipelineactivation)

#### List versions

`GET /api/pipelines/{pipelineId}/versions`

Every version (commit, step and model versions) and the current activation.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200` [PipelineVersionsResponse](#pipelineversionsresponse)

#### Get a version

`GET /api/pipelines/{pipelineId}/versions/{number}`

One version, including its canonical manifest.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |

**Returns:** `200` [PipelineVersion](#pipelineversion)

#### Get a version's effective pipeline

`GET /api/pipelines/{pipelineId}/versions/{number}/effective`

The version with each `select` resolved by a config snapshot: the steps a run of it would have.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `snapshot` | query | integer |  |

**Returns:** `200` [EffectivePipelineResponse](#effectivepipelineresponse)

### Config

#### Get the config document

`GET /api/pipelines/{pipelineId}/config`

The YAML document as saved, and its snapshot (number, author, time, `restoredFrom`). `snapshot` gets an older one; `yaml` is empty before the first save.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `snapshot` | query | integer |  |

**Returns:** `200` [ConfigDocument](#configdocument)

#### Save the config document

`PUT /api/pipelines/{pipelineId}/config`

Saves `yaml` as the next snapshot. 400 with each problem when the active version rejects it. The answer has the check against the active and the pending version, and the version it activated, if any.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Body** (`application/json`): [SetConfigRequest](#setconfigrequest)

**Returns:** `200` [ConfigSaveResponse](#configsaveresponse)

#### List config snapshots

`GET /api/pipelines/{pipelineId}/config/history`

Newest first.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200` [ConfigSnapshotInfo](#configsnapshotinfo)[]

#### Roll back the config

`POST /api/pipelines/{pipelineId}/config/rollback`

Saves snapshot `snapshot`'s content again as the newest.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Body** (`application/json`): [RollbackConfigRequest](#rollbackconfigrequest)

**Returns:** `200` [ConfigSaveResponse](#configsaveresponse)

#### Check the config

`GET /api/pipelines/{pipelineId}/config/check`

The current document against `version` (default: the pending version, else the active one): validity, whether it is satisfied, problems, referenced and missing secrets, changed defaults and the effective values.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `version` | query | integer |  |

**Returns:** `200` [ConfigCheckResponse](#configcheckresponse)

### Secrets

#### List secrets

`GET /api/pipelines/{pipelineId}/secrets`

The secrets the active version (steps, failure handlers, poll triggers) or the binding uses, plus set but unused ones, each with `declaredBy` and `isSet` (null when Kubernetes can't be read). Values are never returned.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200` [PipelineSecretInfo](#pipelinesecretinfo)[]

#### Set a secret

`PUT /api/pipelines/{pipelineId}/secrets/{name}`

Sets the value. Re-checks a pending version.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `name` | path | string | yes |

**Body** (`application/json`): [SetSecretRequest](#setsecretrequest)

**Returns:** `200`

#### Delete a secret

`DELETE /api/pipelines/{pipelineId}/secrets/{name}`

Deleting a secret the current config references needs `force=true` (409 without). Re-checks a pending version.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `name` | path | string | yes |
| `force` | query | boolean |  |

**Returns:** `200`

#### Copy secrets from another pipeline

`POST /api/pipelines/{pipelineId}/secrets/copy`

Copies values server-side from `sourcePipelineId` (`names`, default all; `targetName` renames a single one). Existing names are refused without `overwrite`. Answers the copied names. Whoever can write the target pipeline can read the copied values from its steps.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Body** (`application/json`): [CopySecretsRequest](#copysecretsrequest)

**Returns:** `200` [CopySecretsResponse](#copysecretsresponse)

### Runs

#### List runs

`GET /api/pipelines/{pipelineId}/runs`

Runs of the pipeline, newest first, paged.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `Page` | query | integer |  |
| `PageSize` | query | integer |  |

**Returns:** `200` [PageOfRunSummary](#pageofrunsummary)

#### Start a run

`POST /api/pipelines/{pipelineId}/runs`

Starts a manual run; the body is the pipeline input, validated against the input model. `outcome` is `started` or `queued` (during a draining switch).

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Body** (`application/json`): object

**Returns:** `200` [TriggerRunResponse](#triggerrunresponse)

#### Get the critical path of a range of runs

`GET /api/pipelines/{pipelineId}/runs/critical-path`

Per run, each stage's critical step (the one that finished last) and its time; per step, how often it was on the path and its share of the total. `from` and `to` are run numbers (default: the latest 20 runs, at most 500); without `from`, `sinceVersion` starts at the first run on that pipeline version or a later one. Only succeeded runs unless `includeUnsuccessful`. A stage counts one pass of attempts: automatic retries are included, later reruns are not.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `from` | query | integer |  |
| `to` | query | integer |  |
| `includeUnsuccessful` | query | boolean |  |
| `sinceVersion` | query | integer |  |

**Returns:** `200` [CriticalPathReport](#criticalpathreport)

#### Get a run

`GET /api/pipelines/{pipelineId}/runs/{number}`

Input, pinned version, commit and config snapshot, links, whether it can be continued, and its stages with each step's status.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |

**Returns:** `200` [RunDetail](#rundetail)

#### Cancel a run

`POST /api/pipelines/{pipelineId}/runs/{number}/cancel`

Cancels the run and its scheduled and in-progress attempts (their Jobs are deleted).

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |

**Returns:** `200` [Run](#run)

#### Rerun a run

`POST /api/pipelines/{pipelineId}/runs/{number}/rerun`

Starts a new run with the same input (`rerunOf` points back).

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |

**Returns:** `200` [TriggerRunResponse](#triggerrunresponse)

#### Continue a run

`POST /api/pipelines/{pipelineId}/runs/{number}/continue`

Continues the run with `input` merged over its stored input. Fields consumed by completed steps can't change; the run's version must be active.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |

**Body** (`application/json`): [ContinueRunRequest](#continuerunrequest)?

**Returns:** `200` [Run](#run)

#### List a run's attempts

`GET /api/pipelines/{pipelineId}/runs/{number}/attempts`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |

**Returns:** `200` [Attempt](#attempt)[]

### Steps

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

#### List a step's attempts in a run

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/attempts`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Returns:** `200` [Attempt](#attempt)[]

#### Rerun a step

`POST /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/rerun`

A new attempt of the step on the run's pinned version and pool. With `continue`, the run goes on downstream afterwards.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Body** (`application/json`): [RerunStepRequest](#rerunsteprequest)?

**Returns:** `200` [Attempt](#attempt)

#### Cancel a step

`POST /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/cancel`

Cancels the step's scheduled or in-progress attempt (its Job is deleted). The run ends Cancelled once the stage settles.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |

**Returns:** `200` [Attempt](#attempt)

#### List a step's files

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/files`

The files an attempt stored.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200` [AttemptFilesResponse](#attemptfilesresponse)

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

#### Get a step's output

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/output`

The output JSON of `attempt` (default: the latest successful attempt).

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200` [AttemptOutputResponse](#attemptoutputresponse)

#### Get a step's log

`GET /api/pipelines/{pipelineId}/runs/{number}/steps/{step}/logs`

The attempt's log; the live pod log while it runs.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `number` | path | integer | yes |
| `step` | path | string | yes |
| `attempt` | query | integer |  |

**Returns:** `200` string

#### List blocked steps

`GET /api/pipelines/{pipelineId}/blocked-steps`

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |

**Returns:** `200` string[]

#### Block a step

`POST /api/pipelines/{pipelineId}/steps/{step}/block`

Runs that reach the step are held there until it is unblocked. Answers the blocked step names.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `step` | path | string | yes |

**Returns:** `200` string[]

#### Unblock a step

`POST /api/pipelines/{pipelineId}/steps/{step}/unblock`

Held runs resume. Answers the blocked step names.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `step` | path | string | yes |

**Returns:** `200` string[]

#### Get a step's history

`GET /api/pipelines/{pipelineId}/steps/{step}/history`

The step's attempts across runs, with markers where the step's definition changed.

| Parameter | In | Type | Required |
|---|---|---|---|
| `pipelineId` | path | string | yes |
| `step` | path | string | yes |
| `limit` | query | integer |  |

**Returns:** `200` [StepHistoryItem](#stephistoryitem)[]

### Attempts

#### List running attempts

`GET /api/attempts`

With `status=active`: every scheduled or in-progress attempt across pipelines, oldest first.

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

#### Liveness

`GET /api/health`

`status`, `image` (`Deployment:Image`, set by the compose deployer) and the server `version`.

**Returns:** `200`

#### Readiness

`GET /api/ready`

200 once persisted state is loaded, 503 before.

**Returns:** `200`

#### Get the login settings

`GET /api/config/frontend`

The OIDC authority and client id the web UI and `pl login` use.

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
| `code` | string | required |
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

### AttemptFileEntry

| Field | Type | |
|---|---|---|
| `path` | string | required |
| `size` | integer | required |
| `lastModified` | string (date-time)? | required |

### AttemptFilesResponse

| Field | Type | |
|---|---|---|
| `run` | integer | required |
| `step` | string | required |
| `attempt` | integer | required |
| `files` | [AttemptFileEntry](#attemptfileentry)[] | required |

### AttemptOutput

| Field | Type | |
|---|---|---|
| `key` | string | required |
| `json` | string | required |

### AttemptOutputResponse

| Field | Type | |
|---|---|---|
| `run` | integer | required |
| `step` | string | required |
| `attempt` | integer | required |
| `status` | string | required |
| `output` | any? | required |
| `outputKey` | string? | required |

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

### AttemptStatusCancelled

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time)? | required |
| `cancelledAt` | string (date-time) | required |

### AttemptStatusFailed

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time) | required |
| `finishedAt` | string (date-time) | required |
| `reason` | string? |  |

### AttemptStatusInProgress

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time) | required |

### AttemptStatusObsolete

| Field | Type | |
|---|---|---|
| `supersedingAttemptId` | string (uuid) | required |

### AttemptStatusScheduled

| Field | Type | |
|---|---|---|
| `submissionAttempts` | integer |  |

### AttemptStatusSkipped

| Field | Type | |
|---|---|---|
| `reason` | string | required |
| `startedAt` | string (date-time)? | required |
| `finishedAt` | string (date-time) | required |

### AttemptStatusSucceeded

| Field | Type | |
|---|---|---|
| `startedAt` | string (date-time) | required |
| `finishedAt` | string (date-time) | required |

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

### ConfigCheckResponse

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
| `snapshot` | [ConfigSnapshotInfo](#configsnapshotinfo)? | required |
| `yaml` | string | required |

### ConfigSaveResponse

| Field | Type | |
|---|---|---|
| `snapshot` | [ConfigSnapshotInfo](#configsnapshotinfo) | required |
| `active` | [ConfigCheckResponse](#configcheckresponse)? | required |
| `pending` | [ConfigCheckResponse](#configcheckresponse)? | required |
| `activatedVersion` | integer? | required |

### ConfigSnapshotInfo

| Field | Type | |
|---|---|---|
| `number` | integer | required |
| `author` | string | required |
| `createdAt` | string (date-time) | required |
| `restoredFrom` | integer? | required |

### ContinueRunRequest

| Field | Type | |
|---|---|---|
| `input` | object? |  |

### CopySecretsRequest

| Field | Type | |
|---|---|---|
| `sourcePipelineId` | string (uuid) | required |
| `names` | string[]? |  |
| `targetName` | string? |  |
| `overwrite` | boolean |  |

### CopySecretsResponse

| Field | Type | |
|---|---|---|
| `copied` | string[] | required |

### CreatePipelineWithRepoRequest

| Field | Type | |
|---|---|---|
| `repo` | string | required |
| `branch` | string? | required |
| `path` | string? | required |
| `credentialsSecret` | string? | required |

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

### EffectivePipelineResponse

| Field | Type | |
|---|---|---|
| `version` | integer | required |
| `configSnapshot` | integer? | required |
| `selected` | map of string | required |
| `manifest` | string | required |
| `cases` | map of string[] | required |

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

### PageOfRunSummary

| Field | Type | |
|---|---|---|
| `items` | [RunSummary](#runsummary)[] | required |
| `pageNumber` | integer | required |
| `pageSize` | integer | required |
| `totalCount` | integer | required |
| `totalPages` | integer |  |
| `hasNextPage` | boolean |  |
| `next` | [Pagination](#pagination) |  |

### Pagination

| Field | Type | |
|---|---|---|
| `page` | integer |  |
| `pageSize` | integer |  |
| `offset` | integer |  |

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
| `description` | string? |  |

### PipelineActivation

| Field | Type | |
|---|---|---|
| `type` | string | required |

### PipelineActivationActive

| Field | Type | |
|---|---|---|
| `versionId` | string (uuid) | required |
| `versionNumber` | integer | required |
| `since` | string (date-time) | required |

### PipelineActivationDraining

| Field | Type | |
|---|---|---|
| `fromVersionId` | string (uuid) | required |
| `fromVersionNumber` | integer | required |
| `toVersionId` | string (uuid) | required |
| `toVersionNumber` | integer | required |
| `cancelRunning` | boolean | required |
| `since` | string (date-time) | required |

### PipelineBindingStatus

| Field | Type | |
|---|---|---|
| `pipelineId` | string (uuid) | required |
| `repo` | string | required |
| `branch` | string | required |
| `path` | string | required |
| `lastPushSha` | string? | required |
| `lastReconciledSha` | string? | required |
| `result` | integer | required |
| `lastSyncTime` | string (date-time)? | required |
| `problems` | string[] | required |
| `warnings` | string[] | required |
| `reconcileSha` | string? | required |
| `reconcileInProgress` | boolean | required |
| `reconciledVersion` | integer? | required |
| `activation` | [PipelineActivation](#pipelineactivation)? | required |
| `pending` | [PendingVersionInfo](#pendingversioninfo)? |  |

### PipelineConfigBinding

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
| `webhookSecret` | string? |  |

### PipelineHealth

| Field | Type | |
|---|---|---|
| `status` | string | required |
| `lastRun` | [RunSummary](#runsummary)? | required |
| `lastSettledRun` | [RunSummary](#runsummary)? | required |
| `runningRuns` | integer | required |
| `activation` | [PipelineActivation](#pipelineactivation)? | required |
| `draining` | boolean | required |
| `blockedSteps` | string[] | required |
| `heldRuns` | [RunSummary](#runsummary)[] | required |
| `pending` | [PendingVersionInfo](#pendingversioninfo)? |  |

### PipelineSecretInfo

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

### PipelineSummary

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `name` | string | required |
| `description` | string? | required |
| `repo` | string? | required |
| `health` | [PipelineHealth](#pipelinehealth) | required |
| `stages` | [PipelineStageSummary](#pipelinestagesummary)[] | required |

### PipelineVersion

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `pipelineId` | string (uuid) | required |
| `number` | integer | required |
| `manifest` | string | required |
| `manifestHash` | string | required |
| `commitSha` | string? | required |
| `binaryKey` | string | required |
| `stepVersions` | map of integer | required |
| `modelVersions` | map of integer | required |
| `createdAt` | string (date-time) | required |

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

### PipelineVersionsResponse

| Field | Type | |
|---|---|---|
| `activation` | [PipelineActivation](#pipelineactivation)? | required |
| `versions` | [PipelineVersionSummary](#pipelineversionsummary)[] | required |

### ReconcileStatus

| Field | Type | |
|---|---|---|
| `result` | integer | required |
| `lastSyncTime` | string (date-time)? | required |
| `problems` | string[] | required |
| `warnings` | string[]? |  |
| `sha` | string? |  |
| `inProgress` | boolean |  |
| `versionNumber` | integer? |  |

### RerunStepRequest

| Field | Type | |
|---|---|---|
| `continue` | boolean |  |

### RollbackConfigRequest

| Field | Type | |
|---|---|---|
| `snapshot` | integer | required |

### Run

| Field | Type | |
|---|---|---|
| `id` | string (uuid) | required |
| `pipelineId` | string (uuid) | required |
| `number` | integer | required |
| `versionId` | string (uuid) | required |
| `versionNumber` | integer | required |
| `input` | string | required |
| `versionStamp` | string | required |
| `status` | `"Running"` \| `"Succeeded"` \| `"Failed"` \| `"Cancelled"` \| `"Superseded"` | required |
| `trigger` | [RunTrigger](#runtrigger) | required |
| `createdAt` | string (date-time) | required |
| `finishedAt` | string (date-time)? |  |
| `supersededBy` | string (uuid)? |  |
| `rerunOf` | string (uuid)? |  |
| `heldAt` | string? |  |
| `resumedAt` | string (date-time)? |  |
| `configSnapshot` | integer? |  |
| `config` | string? |  |

### RunDetail

| Field | Type | |
|---|---|---|
| `run` | [RunSummary](#runsummary) | required |
| `versionId` | string (uuid) | required |
| `commitSha` | string? | required |
| `input` | any | required |
| `continuable` | boolean | required |
| `stages` | [RunStageView](#runstageview)[] | required |
| `config` | string? |  |

### RunOrigin

| Field | Type | |
|---|---|---|
| `noticedAt` | string (date-time) | required |
| `via` | string | required |
| `pushedAt` | string (date-time)? |  |
| `configReadyAt` | string (date-time)? |  |
| `mapStartedAt` | string (date-time)? |  |
| `mapFinishedAt` | string (date-time)? |  |

### RunStageCondition

| Field | Type | |
|---|---|---|
| `field` | string | required |
| `values` | any[] | required |

### RunStageView

| Field | Type | |
|---|---|---|
| `index` | integer | required |
| `when` | [RunStageCondition](#runstagecondition)[] | required |
| `steps` | [RunStepView](#runstepview)[] | required |

### RunStepView

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

### RunTrigger

| Field | Type | |
|---|---|---|
| `kind` | `"Push"` \| `"Poll"` \| `"Manual"` \| `"Webhook"` | required |
| `dedupKey` | string? |  |
| `origin` | [RunOrigin](#runorigin)? |  |

### SetConfigRequest

| Field | Type | |
|---|---|---|
| `yaml` | string | required |

### SetSecretRequest

| Field | Type | |
|---|---|---|
| `value` | string | required |

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

### SwitchVersionRequest

| Field | Type | |
|---|---|---|
| `version` | integer | required |
| `cancelRunning` | boolean |  |

### TriggerRunResponse

| Field | Type | |
|---|---|---|
| `outcome` | string | required |
| `run` | [Run](#run)? | required |

### UpdateBindingRequest

| Field | Type | |
|---|---|---|
| `credentialsSecret` | string? | required |
