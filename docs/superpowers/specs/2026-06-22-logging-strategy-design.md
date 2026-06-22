# Logging strategy for Olve.Pipelines

**Date:** 2026-06-22
**Status:** Approved (advisor-reviewed) — ready for implementation
**Scope:** Olve.Pipelines application logging, end to end
**Related:** Olve.Homelab OpenObserve logging spec (`cb4b764`). Olve.Pipelines is the
second app onboarded to that stack, after QuestionBank.

## Goal

Make Olve.Pipelines emit logs that are (a) **searchable as structured fields** once
scraped into OpenObserve, (b) a clean **operational audit trail**, and (c)
**backend-agnostic** — the app never names or depends on OpenObserve/OTLP for
shipping. Logs go to **stdout**; the node's OTel Collector DaemonSet (owned by
Olve.Homelab) scrapes and ships them.

## Hard constraints

1. **Application independence.** No OpenObserve/OTLP endpoint, token, org, or stream
   name may appear in this repo *for log shipping*. The only permitted change is
   emitting JSON to stdout. (The pre-existing OTLP **push** exporter in
   `TelemetryConfiguration.cs` → `otel.ovea.pro` is legacy and orthogonal — leave it,
   do not extend it for OpenObserve.)
2. **AOT-safe** (`PublishAot=true`). No reflection-based serialization. The built-in
   console `json` formatter and `ILogger` scopes are AOT-safe. A *custom* formatter
   calling `JsonSerializer.Serialize(object)` without a `JsonTypeInfo` would break AOT
   — avoid.
3. **Homelab scale.** Single node, single operator. Right-size; no distributed-tracing
   machinery.

## Current state (audit 2026-06-22)

**Strengths (keep):** 100% structured `ILogger<T>` with named placeholders (no string
interpolation); domain IDs attached per-message; exceptions passed as objects; Result
problems are logged (not swallowed); log levels broadly sane; HTTP middleware
(`Program.cs:33`) logs `/api` requests with method/path/status/elapsed.

**Gaps:**
- **#1 — default console formatter is text, not JSON** → structured fields are lost the
  moment the line is scraped. This is the load-bearing gap.
- Two throwaway `AddConsole()` SDK loggers (`KubernetesConfiguration.cs:52`,
  `StorageConfiguration.cs:57`) bypass app logging config and will emit **non-JSON
  lines** into the stream once #1 is fixed.
- `ResultProblem[]` logged via a single `{Problems}` placeholder → renders as a string
  blob, not queryable fields.
- K8s job log lines carry the **hex job UUID only**, no StepName/PipelineId.
- Silent paths: webhook signature failures (bare 401), reconcile completion, event
  firing.
- No scopes, no correlation IDs.

## Target strategy (gap-free)

### 1. Output — JSON to stdout via the framework's native formatter
Use the built-in `Logging:Console:FormatterName=json` (values `json` / `simple`).
Deployed envs set `Logging__Console__FormatterName: "json"` via helm env; local dev
omits it (defaults to `simple`/pretty). **Zero app C#.** Also set
`Logging:Console:Json:UseUtcTimestamp=true`. This is the analogue of QuestionBank's
`QB_LOG_FORMAT=json`, using the native feature instead of a custom key.

### 2. Level semantics (contract)
| Level | Meaning |
|---|---|
| Critical | Data corruption / manual intervention required (e.g. corrupt snapshot failing startup). |
| Error | An operation failed and needs attention; **all unhandled background exceptions** (IRunOnStartup, event handlers, pollers) — these must never vanish. |
| Warning | Recoverable / degraded / intentionally gated (blocked promotion, transient retry, drain timeout). |
| Information | Lifecycle milestones an operator cares about (job created/completed, trigger fired, reconcile done) — reads as an audit trail. |
| Debug | Loop iterations, event firing, gate reads — off in prod. |
| Trace | Wire-level (HTTP bodies) — never in prod. |

### 3. Message style
Stable templates (no interpolation — already the norm, now codified, so the backend can
group by template). Always log **human-readable names alongside UUIDs** (K8s job
creation logs `StepName` + `PipelineId`, not just the hex job name). **Never log secret
values** — names only.

### 4. Coverage & always-on security events
Lifecycle → Info (audit trail); loops / event-firing → Debug; **security events
(webhook signature mismatch, auth rejection) → Warning and ALWAYS logged** (currently
silent 401s); persistence saves at a consistent level. **Fix the two bypass
`AddConsole()` SDK loggers** (route through the app logger factory or drop) so they do
not pollute the JSON stream.

### 5. Result-failure logging
Flatten `ResultProblem[]` into **scalar fields** — `{ProblemCount}`,
`{ProblemMessages}` (joined brief strings), `{MaxSeverity}` — **not** the raw array
(which `ToString()`-flattens to a blob). Coordinate with the existing
`Olve.Logging.ResultProblemExtensions` upstream rather than reinventing. Migrate the
known `{Problems}` sites: `StartupRunner.cs:12`, `PipelineBindingEndpoints.cs:114`,
`PollTriggerService.cs:121`.

### 6. Operation outcome + duration
Generalize the proven HTTP-middleware timing pattern (`Program.cs:33`) to `JobRunner`,
`ReconcileCoordinator`, and the poll cycle: log **outcome + duration** on completion
(Info success / Warning|Error failure with reason + context). **Mandatory:**
outcome+duration. **Optional:** a start-log, only for hang-prone async ops (job
execution, reconcile, poll) so "began but never finished" is visible.

### 7. Ambient scopes — OPTIONAL, validate first
Only after confirming the Collector lifts scope fields into OpenObserve: add
**dictionary** scopes (`BeginScope(new Dictionary<string,object>{...})` — *not*
string-template scopes, which the JSON formatter renders as a non-queryable string
array) at **two boundaries only** — HTTP request and job execution — and enable
`IncludeScopes`. **Keep load-bearing IDs (JobId/PipelineId) on messages regardless;**
scopes are a supplement, not the system of record for correlation.

### 8. Testing
**One** test: with `FormatterName=json`, a log line is valid JSON with the expected
top-level fields and structured args surfaced as fields.

## Explicitly cut (over-engineering for this context)
- **Activity-based correlation IDs for logs** — YAGNI at single-node scale; domain IDs
  (JobId/PipelineId) are the real correlation key, and HTTP TraceId is already free via
  existing instrumentation. (The OTel *traces* signal stays as-is, orthogonal.)
- **A custom `Logging:Format` config key** — the native `FormatterName` already does it.
- **Scopes at 5 boundaries / dropping per-message IDs** — the default formatter renders
  scopes as non-queryable strings; risky.
- **A start-log on every operation** — noise.

## Close-out checklist (current → target), in value order
1. **[enabler]** Native JSON toggle: `Logging__Console__FormatterName: "json"` in
   `helm/values.yaml` + `helm/values-beta.yaml`. Deploy. ← the backend-ingestion
   milestone, ~10 lines of YAML, zero C#.
2. **Message quality + silent gaps:** names-alongside-UUIDs; webhook-auth-failure +
   reconcile-completion logs; fix the two bypass `AddConsole()` loggers.
3. **`LogProblems` flattening helper** + migrate the 3 `{Problems}` call sites.
4. **Outcome + duration** on `JobRunner` / `ReconcileCoordinator` / poll cycle.
5. **(Optional, gated on collector validation)** dictionary scopes at HTTP + job
   boundaries.
6. **One JSON-shape test**; add a Logging section to `CLAUDE.md` / `README`.

## Open assumption to validate
The Olve.Homelab Collector's `filelog` receiver JSON-parses the container message (the
spec says it does) — confirm it lifts our JSON fields (and, if scopes are adopted, scope
fields) into **queryable** OpenObserve columns before relying on them.

## Part 2 — ingestion onboarding (deferred, gated on the Homelab stack)
Once QuestionBank logging is live — i.e. the Olve.Homelab OpenObserve + Collector stack
(spec `cb4b764`) is deployed and QB logs are flowing:
- **Verify namespace scope.** The Collector "watches all namespaces minus an exclude
  list". The app pod runs in `apps` / `apps-beta`; the pipeline **step jobs** run in
  `olve-runners` / `olve-runners-beta`. The step-job namespaces carry the actual
  build/deploy output — **the highest-value logs this service produces** — so ensure
  `olve-runners*` is **not** excluded and ideally gets its own stream.
- **Smoke check.** Trigger a pipeline; confirm both the controller's logs and a step
  job's logs appear in OpenObserve with namespace/pod attributes attached.
- This is **Olve.Homelab-side config**; the only Olve.Pipelines prerequisite is Part 1
  step 1 (JSON formatter) deployed.
