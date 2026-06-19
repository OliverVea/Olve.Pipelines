# Failure handlers — implementation plan

Internal working notes (repo-only; **not** served at `/docs`, which is `docs/setup/**` only).
Design + implementation plan for **on-failure handler scripts**: reusable scripts that run
when a pipeline (or a specific step) fails. The motivating first handler spins up an
[Agent of Empires](https://www.agent-of-empires.com) triage session, but the mechanism is general.

Last updated: 2026-06-18 (Phases 1–5 implemented; see status below).

## Implementation status (2026-06-18)

Phases 1–5 are implemented and unit-tested; Phase 6 observability is partial (app logs + a
`olve-role=failure-handler` Job label, no UI). Two decisions were taken during implementation:

- **aoe routing — in-cluster Service DNS, no secrets.** Phase-1 verification found `aoe-private.ovea.pro`
  is **not** unauthenticated-behind-Tailscale: it sits behind an **Authentik forward-auth outpost** on
  public ingress (`GET`/`POST /api/sessions` → 302 to `auth.ovea.pro`). Handlers run *inside* the cluster,
  below that ingress, so they reach aoe via its internal Service URL (set as `AOE_BASE_URL` in config). This
  needs aoe's *own* server to run `--no-auth` (Authentik only at the outpost). Result: **no secret plumbing**.
  The `$SECRET:` → secretKeyRef path (§1, §3b) is therefore **deferred**, not built.
- **Run tracking — untracked best-effort.** Handler runs are plain K8s Jobs submitted directly
  (`IKubernetesClient.CreateBareJobAsync`), NOT first-class `Job` entities. No JobQueue/scheduler/obsoletion
  involvement → no recursion (a handler's own failure can't re-fire `OnGroupFailed`) and no step-scheduling
  collisions. Trade-off: handler runs don't appear in the Jobs UI yet.

Code: `src/Olve.Pipelines/FailureHandlers/` (FailureContext, FailureHandlerLibrary, FailureHandlerBinding,
FailureHandlerBindingService, FailureHandlerService). Wired in `JobEventRegistration` (OnGroupFailed) and
`PipelineReconciler` (materialises `failureHandlers:` → `AttachmentStore<Pipeline, FailureHandlerBindings>`).
Validation in `ManifestCompiler`. One correctness change vs §5: the script expands `$PIPELINE_*` in the
operator prompt via `envsubst` (the plan's `printf` was a no-op), so the image installs `curl jq gettext`.

---

## 1. Concept

A **failure handler** is *just a script* — the existing `StepConfiguration(Image, Script,
EnvironmentVariables)` shape — executed as a Kubernetes Job when a failure fires. It reuses the
same execution machinery as normal steps; it is not a bespoke integration.

- **Reusable, shipped in the app.** Handlers are a built-in named library (like the future typed
  templates DotNetBuild/HelmDeploy). A pipeline references one by name and supplies env. `aoe-triage`
  is the first.
- **The script reads env only; it resolves nothing.** That is what makes one script serve every
  pipeline — it never bakes in a pipeline's secret name, URL, or step.
- **Scope:** a handler binds to the **whole pipeline** (any failure) or to **specific step(s)**.

### Locked decisions (2026-06-18)

- Reusable named scripts in Olve.Pipelines, referenced from config.
- Two env-var classes, resolved in different places (§3).
- `$SECRET:NAME` in a handler binding compiles to a K8s **secretKeyRef** on the Job (value injected
  by K8s, app never sees it) — NOT in-app string resolution like `PollTriggerTarget` (the poller
  runs in-app; handlers run as Jobs).
- **No shared secrets** (rejected). Start aoe `--no-auth` behind Tailscale → handler needs no token.
  If auth is wanted later, the token rides the existing **per-pipeline** secret mechanism — no new concept.
- Config is **GitOps** (`.pipelines/config.yaml`, reconciled). Handler binding (script ref + scope +
  env mapping) is in git; any secret *value* is runtime state injected at execution, never in git.

---

## 2. Hook point (already exists, no subscriber)

`JobEvents.OnGroupFailed` fires reliably and currently has **no subscriber**. `JobGroupCompletionService`
fires `OnGroupCompleted` when every job in a group is `Done`, else `OnGroupFailed`. The success analog
`DownstreamTriggerService.HandleGroupCompleted` (subscribed in `JobEventRegistration.Run()`) is the
template to copy.

Failure is first-class: `JobStatus.Failed(StartedAt, FailedAt, Reason)`.

- **Pipeline-wide handlers** → subscribe to `OnGroupFailed`.
- **Per-step handlers** → the group is keyed by step, so resolve the failed step(s) from the failed
  group's jobs. (If finer per-job granularity is ever needed: `OnUpdated` + `Status is Failed`.)

Key files:
- `src/Olve.Pipelines/Jobs/JobEvents.cs` — `OnGroupFailed` event.
- `src/Olve.Pipelines/Jobs/JobEventRegistration.cs` — where the new subscriber wires in.
- `src/Olve.Pipelines/Jobs/JobGroupCompletionService.cs` — fires the event.
- `src/Olve.Pipelines/Jobs/DownstreamTriggerService.cs` — success-path template.
- `src/Olve.Pipelines/Jobs/KubernetesJobExecutor.cs` — runs `(image, script, env)` as a K8s Job.

---

## 3. Env-var contract (the reusability boundary)

**(a) Failure context — injected by the executor.** The script can't compute these; the orchestrator
knows them at failure time. Well-known names, GitHub-Actions style:

```
PIPELINE_ID
PIPELINE_NAME
PIPELINE_FAILED_STEP        # step name
PIPELINE_FAILED_STEP_KIND   # production | processing
PIPELINE_FAILURE_REASON     # JobStatus.Failed.Reason
PIPELINE_LOG_KEY            # S3 key for the failed job's logs
```

(Whole-pipeline scope with multiple failed steps: inject the first/representative failed step, and
consider a `PIPELINE_FAILED_STEPS` comma-list. Decide during impl.)

**(b) Handler config — supplied in the binding's env mapping.** Static values or `$SECRET:NAME`
refs (compiled to secretKeyRef). e.g. for `aoe-triage`: `AOE_BASE_URL`, `AOE_TOOL`, `AOE_REPO_PATH`,
`AOE_PROMPT_TEMPLATE`.

The handler script reads both classes from env and does its work. It resolves nothing itself.

---

## 4. Config schema (`.pipelines/config.yaml`)

Add a `failureHandlers:` list. Parsed by the reconcile path (`PipelineDocument` / `PipelineReconciler`).

```yaml
failureHandlers:
  - handler: aoe-triage          # name from the built-in library
    steps: [build-and-package]   # omit / empty = whole pipeline (any failure)
    env:
      AOE_BASE_URL: https://aoe-private.ovea.pro
      AOE_TOOL: claude
      AOE_REPO_PATH: /repos/olve-pipelines
      AOE_PROMPT_TEMPLATE: "Step $PIPELINE_FAILED_STEP failed: $PIPELINE_FAILURE_REASON. Logs: $PIPELINE_LOG_KEY. Investigate and fix."
```

Materialised into an `AttachmentStore<Pipeline, FailureHandlerBinding>` (binding = handler name +
scope + env map). Reconcile rewrites it like other config; absence = no handlers.

---

## 5. Handler library (built-in named scripts)

A registry mapping handler name → `StepConfiguration` (image + script). Lives in the app so it
deploys with it and is reusable across pipelines. First entry:

- **`aoe-triage`** — a small image with `curl` + the script below. Reads the failure-context env +
  `AOE_*` config, then `POST $AOE_BASE_URL/api/sessions` to spin up a triage session.

```sh
#!/bin/sh
# aoe-triage: open an Agent of Empires session to investigate a pipeline failure.
set -eu
prompt=$(printf '%s' "$AOE_PROMPT_TEMPLATE")   # env vars already expanded by the shell at injection
curl -fsS -X POST "$AOE_BASE_URL/api/sessions" \
  -H 'Content-Type: application/json' \
  -d "$(jq -n \
        --arg path "$AOE_REPO_PATH" \
        --arg tool "${AOE_TOOL:-claude}" \
        --arg title "triage-$PIPELINE_NAME-$PIPELINE_FAILED_STEP" \
        --arg instr "$prompt" \
        '{path:$path, tool:$tool, title:$title, custom_instruction:$instr,
          create_new_branch:true, yolo_mode:true, trust_hooks:true}')"
```

aoe API contract (from source, `src/server/api/sessions.rs`): `POST /api/sessions` body requires
`path` (a repo dir **on the aoe host filesystem** — not a URL) + `tool`; useful optionals
`title`, `custom_instruction` (seed prompt), `create_new_branch`/`base_branch`, `yolo_mode`,
`trust_hooks` (needed if the repo has `on_create` hooks), `scratch` (server-provisioned dir).
Auto-launches. Bearer auth only if aoe is not run `--no-auth`. **Constraint:** the repo must be
present on the aoe host (pre-cloned / recent project), or use `scratch:true` and clone in-script.

---

## 6. Execution & wiring

New `FailureHandlerService` (transient, event-driven), subscribed in `JobEventRegistration.Run()`:

```csharp
events.OnGroupFailed.Subscribe(id =>
    sp.GetRequiredService<FailureHandlerService>().HandleGroupFailed(id));
```

`HandleGroupFailed(Id<JobGroup>)`:
1. Resolve group → pipeline, failed step(s), `Failed.Reason`, log S3 key.
2. Load the pipeline's `FailureHandlerBinding`s; select those whose scope matches (whole-pipeline,
   or step ∈ failed steps).
3. For each match: look up the handler's `StepConfiguration` from the library, build the env =
   failure-context vars + binding env (with `$SECRET:` → secretKeyRef), and run it as a K8s Job via
   the existing executor.

**Best-effort / fire-and-forget:** a handler's own failure must NOT re-trigger handlers or affect
pipeline status. Run on its own job key so it never collides with step scheduling. Decide whether
handler runs are first-class `Job` entities (gives UI + log visibility — preferred for triage) or
untracked K8s Jobs; first-class is more consistent with the codebase.

---

## 7. Phasing

1. **Confirm aoe-private is Tailscale-only** (no public ingress) → run aoe `--no-auth`. Zero secret plumbing.
2. **Env contract + executor injection** of the `PIPELINE_*` failure-context vars.
3. **Handler library** + the `aoe-triage` script/image.
4. **Config schema + reconcile** (`failureHandlers:` → `AttachmentStore<Pipeline, FailureHandlerBinding>`).
5. **`FailureHandlerService` + `OnGroupFailed` wiring**; run matched handlers as K8s Jobs (best-effort).
6. **Observability + tests** — handler runs visible with logs; tests wire store→events→service per the
   no-DI test seam convention.

---

## 8. Open questions

- Handler runs as first-class `Job` vs untracked K8s Job (§6) — lean first-class.
- Whole-pipeline scope with multiple simultaneous failed steps: one handler run with a steps-list env,
  or one run per failed step? (§3)
- aoe repo presence on its host vs `scratch:true` + in-script clone (§5).
- Dedupe: if several steps in a group fail, ensure a whole-pipeline handler fires once, not N times.
