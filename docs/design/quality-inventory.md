# Quality inventory

From a read-only quality review of `main` on 2026-10-26: backend, `pl` CLI, SDK, frontend, tests and docs. Part 1 lists what is wrong today and can be fixed. Part 2 lists the patterns behind those findings, to check for in future changes and reviews.

**fixed** marks items done since, with their commits. **✓** means someone re-read the code after the review and saw the problem there. Unmarked items come from the reviewers alone; confirm each one before fixing it.

At review time all tests passed: SDK 198/198, .NET 974/974. `tsc`, eslint, prettier and `tools/api-reference.cs --check` were clean.

## 1. Fixable now

### Bugs and lost work

| # | Where | What's wrong | Fix |
|---|---|---|---|
| B1 ✓ — **fixed** (b24979c, 3c2f7ba) | `sdk/src/dev/cli.ts:78` | `olve-dev map-trigger` always exits 64. The `else if (mode === "map-trigger")` is attached to the `--config-file` check, so it fires even when `--event-file` is given. | Check `mode === "map-trigger" && !file` on its own; add a `devMain` test. |
| B2 ✓ — **fixed** (c7ea8a7) | `Pipelines/Triggers/PollTriggerService.cs:88-106` | `Observe` saves the new value before `MapAndStartAsync`. If mapping or the run start fails, the change is only logged and is never retried. | Save the value only after the run started or was de-duplicated, as `PushTriggerService` does with `LastPushSha`. |
| B3 ✓ — **fixed** (d32bafb) | `Pipelines/Triggers/WebhookTriggerService.cs:86` | The delivery ID is remembered before the in-flight cap (`:92`). A delivery answered 429, dropped because the config isn't current, or whose mapping failed still counts as seen, so GitHub's Redeliver (same `X-GitHub-Delivery`) is ignored. | Remember the ID only once the delivery is admitted; forget it on Busy, drop or failure. |
| B4 ✓ — **fixed** (2fc0829) | `Shared/Persistence/RunPersistenceService.cs:126` | It subscribes to the stores in `StartedAsync`. The startup sweep (`RunSchedulingStartup`/`RunSweep`) runs earlier, in `StartAsync`. So the sweep's changes (completed drains, queued run starts taken off the queue and started, orphan drops) aren't saved, and a restart can apply them again. | Subscribe in `StartingAsync`, straight after `_loaded = true`. |
| B5 — **fixed** (3133b66) | `Jobs/KubernetesJobExecutor.cs:57-77`, `Jobs/JobRunner.cs:44` | Before submitting, the executor checks the attempt snapshot it started with, not the current status. An attempt made obsolete after its watcher started still gets a Job. Because `JobRunner` skips attempts that already have a watcher before reserving their key, the newer attempt's pod can overlap the older one until the next poll deletes it. | Re-read the status just before `SubmitAsync`; reserve the busy key for any attempt that has a watcher. |
| B6 ✓ — **fixed** (9eca0f9) | `src/Olve.Pipelines.Cli/Commands/Wait/WaitCommands.cs:48` | Any problem from `check` ends `pl wait` at once, and transport errors and 5xx come back as problems. So `pl wait push … --until finished` reports "failed" when prod restarts during its own self-deploy. | Treat transport errors and 5xx as pending, retrying with backoff; fail only on 4xx. |
| B7 — **fixed** (ae2c7bb) | `Shared/Persistence/RunPersistenceService.cs:223` | The 1 s flush timer is `async void`. During an S3 outage, ticks queue on `_flushGate` and each runs a full retry, giving a burst of error logs. | `_flushGate.WaitAsync(0)` (skip when a flush is running), or a `PeriodicTimer`. |
| B8 — **fixed** (ba3702e) | `Pipelines/Scheduling/RunSchedulingEventRegistration.cs:70,77` | `_ = runs.Delete(...)` / `_ = attempts.Delete(...)` throw away failures without logging them. | Log them with `LogProblems`. |

### Security

| # | Where | What's wrong | Fix |
|---|---|---|---|
| S1 — **fixed** (e70083b) | `Pipelines/Triggers/PollTriggerService.cs:32,168,183` | The poll GET runs in the controller pod, outside gVisor and the step NetworkPolicy, and that pod holds K8s and S3 credentials. It follows redirects, which forward `$SECRET` headers to the redirect target, and reads the body with no size cap. Any user the allow-list admits can bind a repo that does this. | Turn off redirects, block private and cluster IP ranges, cap the body. Or run the poll in a sandboxed Job, like `map-trigger`. |
| S2 ✓ — **fixed** (593f4e2) | `Configuration/AuthAllowList.cs:26` | `Auth:Allow:Users` matches `preferred_username`. A GitHub login can be renamed and then registered by someone else, and Dex passes it through. | Match the stable `sub`/user ID as well, or instead. |
| S3 ✓ — **fixed** (aff714d) | `sdk/src/core/models.ts:43` | `FileRef.step` is only `z.string().min(1)`, and it is joined unencoded into paths (`ctx/files.ts:120,130`). `"../.."` escapes the temp or files directory. | `.regex(STEP_NAME)`. |
| S4 — **fixed** (e258ed9) | CLI auth (`src/Olve.Pipelines.Cli`) | The cached bearer token is sent to whatever `--api-url` or `PIPELINES_API_URL` is set to, even when that differs from the profile's stored `ApiUrl`. | Bind the token to the URL it was issued for. |
| S5 — **obsolete**: the OpenBao credentials provider was removed | `Kubernetes/OpenBaoCredentialsProvider.cs:13`, `Configuration/KubernetesConfiguration.cs:68,80` | OpenBao K8s credentials are resolved once and cached for the life of the process, so rotating the token needs a pod restart. | Give the client a refreshing token provider, as in-cluster mode has. |
| S6 — **fixed** (9b208b9) | `Pipelines/Triggers/WebhookTriggerService.cs:65` | When a trigger's secret is unset, every unauthenticated request reads the whole pipeline Secret and answers 503. Only successful reads are cached. | Cache the miss for the recheck window. |

| S7 — **fixed** (fa72794) | `Configuration/AuthAllowList.cs`, `LoginCommand.cs:21`, `frontend/src/auth.ts:62`, `helm/templates/dex-configmap.yaml` | `Auth:Allow:Orgs` likely never matches: neither `pl` nor the UI requests the `groups` scope, and the Dex GitHub connector has no `orgs`/`loadAllGroups`, so tokens carry no `groups` claim. | Request `groups` in both clients and configure the connector, or drop `Orgs` from the docs. |

### Missing features and usability

| # | What's wrong | Fix |
|---|---|---|
| U1 ✓ — **fixed** (5a9febf) | `pl dev` never passes `--config-file` and never sets `OLVE_CASES`, and `DevPlan.Flatten` runs without config. So `pl dev pipeline` can't get through this repo's own `.pipelines/`: `beta-test` needs `config.auth`, and `deploy-beta` sits in several `select` cases. | `pl dev --config <file\|yaml>`; choose the cases from it and set `OLVE_CASES`. |
| U2 | `pl wait push` matches runs on an input field named `sha` (`WaitCommands.cs:330`) and scans only the newest 15 runs (`:258`). Neither limit is documented. | Have the server record the trigger sha and match on that; meanwhile document both limits. |
| U3 — **fixed** (ae0efa3) | An unknown command or too few operands exits 1; an unknown option or a bad flag exits 2 (`CliDispatcher.cs:174-202`). | Route all usage errors through one path; document the exit codes 0/1/2/124/130 in `docs/setup/cli.md`. |
| U4 — **fixed** (ae0efa3) | `pl trigger --input` takes JSON or `@file` (with `--field k=v`), while `pl dev run/pipeline --input` takes `k=v`. | Use `--field` for `k=v` in both places. |
| U5 ✓ — **fixed** (ae0efa3) | `echo $TOKEN \| pl secret set …` keeps the trailing newline (`SecretCommands.cs:124`), which breaks Authorization headers. | Warn when the value ends in `\n`, or add `--trim`. |
| U6 — **hidden** from help and docs (3187dc8); not implemented | `pl dev run --from` is listed in help but always errors (`DevCommands.cs:311`); `docs/setup/cli.md:113` documents it. | Implement it (the run API exists), or hide it. |
| U7 — **fixed** (ae0efa3) | Global flags before the noun (`pl --profile beta run list`) fail. Login has no timeout on the loopback wait, and Ctrl-C there exits 1, not 130. A network blip while polling the device flow throws. With `--verbose`, unhandled exceptions skip the friendly error path. | Small fixes each. |

### Frontend

| # | What's wrong | Fix |
|---|---|---|
| F1 — **fixed** (eb809e0) | Every view polls (`frontend/src/v2/poll.ts`: 10 s by default, 3 s on the run view). Only the log tail uses `/api/events`, which contradicts `docs/setup/runs-and-operations.md:163`. | One shared SSE controller that reloads views on each notice, with polling only as a fallback after a disconnect. |
| F2 | The log tail (`log-pane.ts:95-111`) stops following after a clean disconnect, such as a proxy idle timeout or the service redeploying itself. After an error it polls every 3 s forever and never goes back to the stream. The `retry:` field is ignored (`api.ts:137`). | Reconnect with backoff while the view is still live. |
| F3 | Errors during background reloads are swallowed (`pipeline-list-view.ts:324`, `run-view.ts:344`), so a page can show stale data with no sign that anything is wrong. | A "stale / reconnecting" badge. |
| F4 | An `<a class="badge">` sits inside a `role="button"` card (`stage-graph.ts:907`). | Move the link out, or make the card itself the link. |

### Test gaps

| # | Gap |
|---|---|
| T1 — **fixed** (ebec896) | No test covers the real endpoint auth table. `ReadPolicyTests` only checks a synthetic route; nine `AllowAnonymous` endpoints are unchecked. Walk `EndpointDataSource` and assert exactly which endpoints are anonymous and which need read access. |
| T2 — **mostly fixed** (17625cc; OAuth2/OpenBao providers removed; beta-test end-to-end step still open) | No tests for `OpenBaoCredentialsProvider`, `OAuth2TokenProvider`, `InClusterCredentialsProvider`, `KubernetesPipelineSecretReader`, `FailureContextBuilder` or `ChangeEventRegistration` (the mapping `pl wait` depends on). `HarnessResultCollector`'s size limit and streaming into storage are untested. `beta-test` never runs a step, so real deploys are the only coverage. |
| T3 | The CLI has no tests for login, the OIDC and device flows, the `pl wait` loop (only its parse helpers) or the dispatcher's exit codes. `RefreshingAuthHandler` has one test. |
| T4 | `olve-dev`'s entry point `devMain` has no test; tests go through `runHarness`. That is how B1 slipped through. |
| T5 | Some "not dispatched yet" assertions follow a fixed `Task.Delay(100/200)` and can pass without proving anything (`JobRunnerTests.cs:185,272`, `JobDispatchSignalTests.cs:28`). |
| T6 | No frontend tests. Pure modules to start with: `v2/diff.ts`, `config-schema.ts`, `schema-form.ts`, `api.ts:problemsFromBody`, the stage-graph layout. |
| T7 | 26 uses of `.Value!` in tests (9 in `Harness/HarnessJobBuilderTests.cs`, 4 in `TriggerInputTests.cs`, 3 in `ReconcileApplyTests.cs`). Use `TestRunDomain.Pick` or `SucceededAndValue`. |

### Docs and conventions

| # | What's wrong |
|---|---|
| D1 ✓ — **fixed** (7c9b29f) | `README.md:39` says `git switch v2`. There is no `v2` branch, so the quickstart fails at step one. |
| D2 — **fixed** (3187dc8) | `sdk/README.md:172` mentions v1's deploy-beta loop. `docs/setup/llms.txt` mentions an SDK-publishing limitation that no longer exists. `docs/setup/cli.md:113` documents `--from` (see U6). |
| D3 | About 60 `spec §`/`(B7)` references in comments (for example `StageScheduler.cs:12`, `RunStartService.cs:23`), and long narrative XML docs. |
| D4 | `DateTimeOffset.UtcNow` instead of `TimeProvider` (`DeployPollService.cs:182,237`, `PipelineConfigBindingService.cs:40`). A raw `Guid` in `TriggerMappingService.EventKey`. |
| D5 | Persistence and SSE subscribe to the stores instead of the event hubs. `DeployPollService`, `WebhookTriggerService` and `RetentionService` resolve dependencies through `sp.GetRequiredService`. |
| D6 | Large files that mix responsibilities: `stage-graph.ts` (1013 lines), `run-view.ts` (818), `DevCommands.cs` (576), `WaitCommands.cs` (375), `describe.ts` (565). |

## 2. Patterns to check for

Each pattern names the findings above that it generalises.

### Event intake (triggers, webhooks, cursors)
1. **Move a cursor only after the work is done.** A "seen" marker (poll value, delivery ID, push sha) is written only after the run started or was de-duplicated, never before. (B2, B3)
2. **A rejection leaves nothing behind.** A 429, a drop or a failed mapping must not leave state that stops a retry. (B3)
3. **Cache misses too.** Anything an unauthenticated request can trigger caches its misses as well as its hits. (S6)

### Concurrency and lifecycle
4. **Check again right before I/O.** Before a K8s or S3 side effect, re-read the entity's current status, not a snapshot taken earlier. (B5)
5. **Startup order.** Anything that changes state at startup runs only once persistence is listening. Check the `StartingAsync` → `StartAsync` → `StartedAsync` order. (B4)
6. **No `async void` timers.** Use a `PeriodicTimer`, or skip when the previous tick is still running. (B7)
7. **Never discard a Result.** `_ = x.Delete(...)` is a smell; at least log the problems. (B8)

### Security
8. **Outbound HTTP from the controller.** The controller holds credentials, so a GET to a user-supplied URL has redirects off, a body cap and internal addresses blocked, or runs in a sandboxed Job. (S1)
9. **Identities are stable IDs**, not usernames or display names. (S2)
10. **Validate every part of a path.** Any string joined into a filesystem path gets a restricted pattern. (S3)
11. **Tokens are tied to their origin.** Never send a bearer token to a URL it wasn't issued for. (S4)
12. **Credentials rotate.** Use a refreshing provider, not a token cached for the life of the process. (S5)

### Clients (CLI, UI)
13. **Survive the server restarting.** Waits and long-lived connections retry transport errors and 5xx, because this service redeploys itself mid-run. (B6, F2)
14. **One meaning per flag.** A flag means the same thing in every command, usage errors share one exit code, and the exit codes are documented. (U3, U4)
15. **Help lists only what works.** No flags or features that always error. (U6)
16. **Background errors are visible.** A failed poll or a dropped SSE stream shows a stale indicator rather than leaving old data on screen. (F3)
17. **Docs and code agree.** When they disagree, fix one in the same change. (F1, D1, D2)

### Tests
18. **Every entry point has a test**, not only the function it calls. (T4)
19. **Assert the auth table.** One test lists every endpoint and checks exactly which are anonymous and which need read access. (T1)
20. **No fixed sleep before a "nothing happened" assertion.** Wait for the loop to go idle, or use a fake clock. (T5)
21. **I/O adapters get stub-HTTP tests**, and `beta-test` runs at least one real step end to end. (T2)

### Conventions (in CLAUDE.md; these drifted)
22. No spec or plan references in comments; `TimeProvider`, never `UtcNow`; `Id<T>`, never `Guid`; subscribe to the event hubs, not the stores; no `.Value!` in tests. (D3–D5, T7)
