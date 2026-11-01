# Test suite audit and change plan

Audit of the backend unit tests at commit `143f9e8` (2026-10-31), with a proposed plan. This is analysis only: no code or tests were changed.

Reproduce from `src/backend/Olve.Pipelines.UnitTests`, after `cd src/spec && bun install && bun run build`:

```bash
dotnet stryker                          # full run, about 18 minutes
dotnet stryker -f stryker-control.json  # coverage-off control, about 7 minutes
```

The redundancy, flakiness and control figures come from the scripts in `tools/test-audit/`, whose [README](../../tools/test-audit/README.md) gives the commands.

These findings are a snapshot: line numbers and scores drift as `main` moves.

## Verdict

- **Duplicates are not the main problem.** Of 16 groups of different test methods with identical kill sets, one is a clear merge and one is optional. The other 14 state distinct rules or guards.
- **Gaps are the main problem.** The backend mutation score is 55.06%, and some dangerous behaviour is untested, including the drain guard, the guard against deleting a run that is not finished, token validation and per-change persistence. All 1,061 non-string survivors have been triaged, giving 116 gap rows on top of the first 8, of which 8 are High. The code is correct in every one: these are missing tests, not known bugs.
- **The suite is reliable and already parallel.** No test failed in 156 runs, including under CPU saturation and with four copies running at once.
- **The biggest blind spots are outside this suite.** Real-system behaviour and the frontend have no tests of their own, and those are the likeliest sources of the interface bugs seen in the fix history.

## Standards applied

- Lean towards user-facing flow tests, while keeping builds fast.
- Tests run in parallel by default, and a serial test must justify itself.
- A test stays if it catches something unique, states a distinct user-facing rule, or guards something mutants can't measure (races, ordering, security, a contract with another system).
- A narrow test overlapped by a broader one stays if it names a distinct rule.
- Error message text is behaviour and is asserted.
- A test runs more than once only when its configuration differs.
- Flakes stay under about a 5% suite failure rate.
- Parallelism counts heavily in a test's favour.

## Mutation results

| Status | Mutants |
|---|---|
| Killed | 3,988 |
| Timeout (counts as detected) | 13 |
| Survived | 1,824 |
| NoCoverage | 1,441 |
| CompileError (unmeasured) | 1,876 |
| Ignored | 1,587 |
| RuntimeError | 4 |

Score by area, lowest first among areas with at least 100 measured mutants:

| Area | Score | Measured | Survived | NoCoverage | Unmeasured |
|---|---|---:|---:|---:|---:|
| `Configuration` | 31.7% | 518 | 269 | 85 | 32 |
| `Pipelines` (root) | 42.2% | 154 | 26 | 63 | 46 |
| `Pipelines/Sync` | 48.3% | 468 | 57 | 185 | 205 |
| `Kubernetes` | 48.3% | 1,091 | 287 | 277 | 112 |
| `Pipelines/Retention` | 48.4% | 250 | 92 | 37 | 123 |
| `Jobs` | 52.2% | 157 | 37 | 38 | 41 |
| `Pipelines/Runs` | 54.9% | 499 | 101 | 124 | 191 |
| `Pipelines/Configs` | 55.6% | 266 | 61 | 57 | 80 |
| `Shared` | 56.2% | 432 | 109 | 80 | 130 |
| `Pipelines/Triggers` | 57.7% | 293 | 54 | 70 | 94 |
| `Streaming` | 58.9% | 107 | 25 | 19 | 44 |
| `Pipelines/Scheduling` | 63.1% | 529 | 104 | 91 | 263 |
| `Pipelines/Manifests` | 66.1% | 2,157 | 563 | 169 | 398 |

`GitHub` scores 8.7% on 46 mutants and `Health` 0% on 11. Most of that code talks to real systems, so unit mutation scores understate how it is really exercised.

`Configuration` is low largely by design: its survivors are API metadata (`WithTags`, `WithSummary`) and DI registrations, which flow tests exercise indirectly. `RequireRead()` is not a gap. All 23 statement mutants on those lines were killed by `EndpointAuthTests.OnlyTheseEndpointsUseTheReadPolicy`, and `SetFallbackPolicy(signedIn)` applies the same requirement underneath.

Files with the most undetected mutants worth attention: `ManifestValidator.cs` (232, 54.1%), `ManifestParser.cs` (185, 71.1%), `JsonSchemaValidator.cs` (108, 58.3%), `RunPersistenceService.cs` (89, 52.9%), `HarnessJobBuilder.cs` (72, 54.4%), `RetentionService.cs` (65, 19.8%).

763 survivors are string mutations. Under the decision to assert messages most are real gaps, though some strings are paths or other non-message values. The other 1,061 survivors are logic changes, triaged one by one:

| Verdict | Mutants |
|---|---:|
| New gap, 116 rows (8 High, 50 Medium, 58 Low) | 581 |
| Already under Confirmed gaps | 14 |
| Equivalent | 124 |
| Harmless | 340 |
| Tool artefact (static mutants that an active mutant would fail) | 2 |

906 were read individually and 155 assigned by class. No mutant was re-run, so equivalent and harmless verdicts are reasoning, not measurement. `CriticalPathService`, API errors, `ChangeEventRegistration` and `PodChangeSignals` were judged after reading the function, not mutant by mutant. Per-mutant verdicts are in [`tools/test-audit/triage-ledger.json`](../../tools/test-audit/triage-ledger.json).

### How far to trust these numbers

- **Repeatability:** Manifests ran twice. 2,150 of 2,158 statuses matched, and 1,414 of 1,415 killer sets. All 8 status differences went from Survived to Killed. The one killer-set difference was `SchedulerRuntimeTests.EndToEnd_ThroughJobRunnerAndNoOpExecutor`.
- **Coverage-off control:** every test ran against 367 mutants from 6 files. 364 statuses agreed in substance (NoCoverage reads as Survived when coverage is off). The 3 differences all under-counted detection, so the score may be very slightly low. 68 of 69 killer sets matched.
- **Compile failures:** 1,876 mutants have this status, but 1,031 of them fail to compile even alone, so no mutant exists to measure. The real gap is about 845 mutants (7.9%), not 17.5%. This comes from compiling each mutant alone offline; 162 sit in members with errors in that reconstruction, so the split could move slightly. Of the 845, 439 were lost to Safe Mode, 17 members wiped in full. The other 406 were rolled back with a neighbouring mutant, or are unexplained (94). The code shape that triggers Safe Mode is `StatementMutator` removing a `continue`, `return;` or `throw` that makes a later variable definitely assigned, so `CS0165` lands outside the mutant. 19 such sites in 15 members were confirmed by compiling them alone. `TryPickProblems(out var problems)` is not the cause. The `CS8070` is one `yield break` at `SseStreams.cs:25`. `FlushAsync` has only a candidate trigger, and about 4 of the 24 events are unaccounted for until a rerun with `--diag`. Affected members include `ApplyStageOutcome`, `CheckWhen` and `CheckTriggers`. `StageScheduler.cs` has 41 and `ManifestValidator.cs` 106 unmeasured mutants that do compile alone, so those scores overstate what is known.
- **Hidden mutants:** `ignore-methods: "*Log*"` matches any call with "log" in its name. About 60 of the 536 mutants it removed are not logger calls, including `LogIfFailed(runs.Complete(...))` at `StageScheduler.cs:79,87,170,262,269,283,287`. Those state transitions were never mutated, and `GetLogsAsync` and `PutLogAsync` are hidden the same way.
- **Runner:** the MTP runner is in preview. The checks above are why these results are treated as sound for this suite.

## Confirmed gaps

Each survived or was uncovered, and a search found no test for the behaviour. The current code is correct in every row. Risk is the outcome if the behaviour regressed, not how likely that edit is.

| Location | Untested behaviour | Risk |
|---|---|---|
| `StageScheduler.cs:92` | A drain must stop the scheduler starting work | High |
| `RetentionService.cs:125` | Retention re-checks a run is finished before deleting it | High |
| `StageScheduler.cs:171` | Superseding a run cancels its Scheduled and Waiting attempts | High |
| `RetentionService.cs:143` | Job and Secret sweeps still run when only one list is empty | Medium |
| `RunStartService.cs:68` | `RerunRun` replaces the input when one is given | Medium |
| `ManifestValidator.cs:387` | External inputs are rejected in failure handlers | Medium |
| `ManifestValidator.cs:107-109` | Retention bounds: the 0 boundary, and negative `minRunAgeDays` and `buildGraceDays` | Low |
| `RunStartService.cs:153` | Exact de-dup window boundary | Low |

The coverage-off control also confirmed these files have large undetected areas, because their not-covered mutants still survived when every test ran:

| File | Score | Undetected | What's missing |
|---|---:|---:|---|
| `PipelineReconcileService.cs` | 24.3% | 56 | The build-job half (lines 152-246). `ReconcileApplyTests` covers applying a manifest, but the core GitOps path from build Job to manifest never runs. |
| `BindingHandlers.cs` | 0% | 63 | The binding API handlers |
| `AttemptFileService.cs` | 33.3% | 36 | Serving a step's files |

Assessed as harmless or equivalent: `StageScheduler.cs:300` (guards a log line), `StageScheduler.cs:201` (equivalent), `StageScheduler.cs:298` (rewrites the same value), `StageScheduler.cs:49` (two held runs reach the same outcome in either order). `ManifestValidator.cs:301` is a Low gap, row G08.

### Gaps from the survivor triage

All 116 rows. "Reached through" names the calling path where the effect lands away from the mutated line.

| Risk | ID | Location | Behaviour and bad outcome | Reached through | Mutants | Test search |
|---|---|---|---|---|---:|---|
| High | G25 | `RunPersistenceService.cs:93-94,96-99,101-105,107-109,112,115,118,120,172; RunEventRegistration.cs:21; PipelineVersionEventRegistration.cs:14` | Untested: each state change is saved on its own. A regression dropping one subscription would lose that state across a restart, and this service redeploys itself: a switch would revert, an unblock would re-block, run numbers would repeat and overwrite run.json, a run cancelled with no attempt change would resume. | Stores to event hubs (RunEventRegistration, PipelineVersionEventRegistration) to these subscriptions to FlushAsync | 21 | Restart_* tests set blocked + queued + versions together. Flush_RewritesOnlyChangedRuns changes runs only through attempts, so runs.OnUpdated (115) is unproven. |
| High | G28 | `AuthenticationConfiguration.cs:46-47,50-51,55,57,59` | Untested: an expired token, a wrong issuer and a wrong audience each get 401. A regression would let expired tokens work forever, or accept tokens minted for other clients. | JwtBearer TokenValidationParameters | 11 | No Http test sends such a token. ReadPolicyTests is anonymous only; AuthAllowListTests uses valid tokens with Authority and FrontendAuthority set to the same value. |
| High | G30 | `AuthAllowList.cs:34,38` | Untested: an allow-list without Orgs doesn't admit a user for having a groups claim. A regression (All on an empty list is true) would admit anyone with any group; the email check has the same shape. | signedIn policy, AuthenticationConfiguration.cs:89, to Allows() | 2 | Allow(orgs:[...]) rows always pass one org; users-only rows have no groups claim. |
| High | G34 | `RetentionPlanner.cs:197,201` | Untested: the binary of the Active version and of a drain's target is kept for its own sake. A regression would delete step.tar.gz of an idle pipeline or rollback target with no retained run, and every new run would fail until a rebuild. | RetentionService.SweepAsync to Builds() | 2 | Binaries_KeptFor... gives the active version retained runs (2-21), so that rule hides this one; its Draining target is a version not in the list. |
| High | G115 | `ManifestWriter.cs:192` | Untested: the canonical manifest keeps each select case's stages. A regression would store empty cases: reconcile stores this text as the version's manifest and every run parses it back, so the selected branch's steps would silently not exist and the run would Succeed. | ManifestVersioning.cs:46 to PipelineReconcileService.cs:102 (stored) to RunPlan.cs:246 (parsed for every run) | 1 | SelectTests round-trip only checks Parse(written) re-writes to the same text, which holds for empty cases. The byte-identity fixtures have no select. Reconcile has no hash cross-check: ManifestHash is compared only with earlier versions (PipelineReconcileService.cs:91,95), never with describe's bytes. |
| High | G117 | `DeployPollService.cs:135` | Untested: a sync releases the binding's lock. A regression would make the next sync of that binding wait forever: polling, reconcile-now and every automatic run start behind RunIfConfigCurrentAsync would hang, so that pipeline's deploys stop silently. | PollSource 'binding:<id>' (:70) and ReconcileNowAsync (:90) to SyncConfigAsync; RunIfConfigCurrentAsync waits on the same lock (:149) | 1 | Three tests reach it and none syncs the same binding again (a second sync would hang and show as Timeout, not Survived). |
| High | G12 | `ManifestValidator.cs:587` | Untested: the CheckShape depth guard on recursive schemas. JsonSchemaShapes follows local $ref, so a regression would let a self-referencing model overflow the stack, which can't be caught. One repo's push would crash-loop the service, because reconcile runs again at startup. | PipelineReconcileService:78 to ManifestValidator.Validate to CheckShape | 1 | No test has a recursive $ref schema; the L574 return is NoCoverage (5308). The only $ref test is JsonSchemaValidator rejecting it. |
| High | G45 | `RunPlan.cs:67` | Untested: a run of a manifest with a select plus ordinary stages uses the effective plan. A regression would make ForRun return the base plan, which has no select steps: the branch's deploy step would silently never run and the run would Succeed. | RunPlanCache.ForRun at RunPlan.cs:201, also ConfigValidation.BranchNeeds:103 | 1 | No test runs a select manifest through plans.ForRun. SelectTests calls RunPlan.ForConfig directly; grep ForRun: ExternalInputTests only, no select. |
| Medium | G13 | `ConfigRefs.cs:50,56-57,61-64,66,70,73-74; ManifestValidator.cs:624,626,628` | Untested: a step referencing part of a config entry needs only that part's secrets, and a $case narrows a union to one branch. A regression would refuse valid library steps, or pass a step that needs a secret without declaring it. | CheckStepConfig, ManifestValidator.cs:630, to ConfigRefs.SubSchemas | 24 | Only the negative StepReceivingASecretRef_MustDeclareTheSecret; ConfigRef_ToAField declares all secrets. |
| Medium | G76 | `ServiceConfiguration.cs:48,55,59-61,70,82-83,85,138,151,163,165,171,183,198-199,203` | Untested: hosted-service and IRunOnStartup registrations. A regression would silently lose persistence, retention, latest-wins, failure handlers or poll triggers, with no exception. Beta-test catches some, not persistence across restart. | Program startup | 18 | ProductionWiringTests resolves services and asserts scheduling startup; nothing asserts the hosted list. |
| Medium | G80 | `JsonSchemaShapes.cs:34,39,41,46,54,57,150,164,171` | Untested: field resolution through unions, records and z.any/z.unknown schemas, and integer folded into number. A regression would pass a ref to a field that doesn't exist, lose type checks through unions, or refuse valid refs and mappings. | ManifestValidator CheckMapping (:511), CheckConfigRefs (:655), CheckShape | 15 | Rows use plain object models. PlainInput_FromNullable... is positive only; additionalProperties appears only as false; grep integer: only test JSON schema. |
| Medium | G05 | `ManifestValidator.cs:381,389,391,396,398,400` | Untested: a valid person, callback and poll external input passes validation. A regression adding a false error would make reconcile reject every pipeline using external inputs, stalling its deploy. Also unasserted: reserved '$' keys ($config is the step's config slice), a duplicate kind beside another kind, an unsafe callback key beside another source, empty valuePath. | PipelineReconcileService:78 | 10 | ManifestValidatorTests has only negative rows; 5108 is covered by EachCheck only, and the '$' error line is NoCoverage (5109). ExternalInputTests does not call the validator. |
| Medium | G125 | `HarnessJobFactory.cs:28,33,42,44` | Untested: the attempt job carries input, file, callback URLs and OLVE_CASES from the factory. A regression would fail file and callback steps, and same-named select steps (the SDK refuses without OLVE_CASES). | KubernetesJobExecutor.cs:104 to CreateStepJobAsync | 8 | Executor tests assert the failure-handler job; SelectTests:142 builds a request by hand, skipping the factory. |
| Medium | G103 | `GitHubConfigSource.cs:112,141,160-162,170` | Untested: GitHub requests carry the token, User-Agent and Accept headers. A regression would stop private repos syncing, or get 403s from GitHub, which requires a User-Agent. A fake can't show this, so it needs a recorded fixture. | DeployPollService and PushTriggerService to GitHubConfigSource | 7 | Tests use a fake handler; grep Authorization, User-Agent, Accept in those tests: none. |
| Medium | G17 | `RetentionService.cs:76-77,223` | Untested: the service wires the Kubernetes inventory into the plan. A regression would silently stop sweeping Jobs and URL Secrets, which hold pre-signed URLs. Broader than the doc's :143 row: no service test has an inventory, so the whole half never runs. | SweepAsync to RetentionPlanner.Plan | 6 | RetentionServiceTests registers no IKubernetesInventory or IKubernetesClient. Planner tests cover the plan only. |
| Medium | G126 | `HarnessOptions.cs:77,98,101` | Untested: default values of MapTriggerTtlSeconds, MaxArchiveBytes and MaxTotalBytes. A regression to TTL 0 would delete a map-trigger Job before its log is read, and every automatic trigger would fail. Shrunken caps would refuse normal artifacts. | HarnessJobBuilder.cs:304; HarnessResultCollector, HarnessResultArchive | 5 | Tests pass custom limits; the TTL assertion is on the failure-handler job. |
| Medium | G118 | `ConfigurationPersistenceService.cs:191` | Untested: LastPushSha and LastReconciledSha survive a restart when stored under their current names. A regression would lose them, so a restart would re-fire the push trigger and reconcile again. | LoadAsync | 4 | ConfigurationPersistenceServiceTests:221 covers only pre-rename snapshots. |
| Medium | G120 | `HarnessFailures.cs:62,66-67` | Untested: a finished Job with a non-Failed condition, which real Kubernetes always sends. A regression would throw on, or misread, every Complete Job. The fake data only has a Failed condition. | KubernetesClient.Harness.cs:95 to HarnessJobControl.GetStateAsync | 4 | HarnessOutcomeTests:83 has one Failed condition. |
| Medium | G40 | `SwitchService.cs:72,104,119` | Untested: --cancel-running also cancels idle or held runs on other versions, and leaves runs on the target version alone. A regression would let a held old run carry on, or cancel a run on the new version. | TryCompleteDrain | 4 | Switch_CancelRunning_* has one old run with an in-flight attempt and none on the target. |
| Medium | G42 | `AttemptService.cs:208-210; ExternalInputService.cs:44` | Untested: a step with two external inputs waits for both and keeps each answer, and an answered input can't be re-answered. A regression would start it on the first answer without the second approval, or drop the first answer. | ExternalInputService.Answer to AttemptService.Answer | 4 | All ExternalInputTests have one input; the second Answer at :110 fails on 'not waiting', not on this check. |
| Medium | G74 | `TriggerMappingService.cs:90; HarnessJobFactory.cs:93; HarnessOptions.cs:91; HarnessFailureHandlerLauncher.cs:52` | Untested: late URL expiry and wait timeouts add the grace. A regression would give a long step's result PUT a 403 at the very end, and abandon mappers and handlers early. | HarnessJobFactory.cs:25 (LateUrlExpiry), TriggerMappingService.cs:90 | 4 | Only file-URL expiry is asserted (HarnessFileRefsTests); no test on LateUrlExpiry or these sums. |
| Medium | G11 | `ManifestValidator.cs:550,586-587` | Untested: checks recurse into nested fields (a bad ref inside an object mapping, a nested type mismatch). A regression would pass a broken mapping at reconcile, to fail at run time. | CheckMapping, CheckShape | 3 | Rebuilt* tests use good refs and test missing top-level fields. No nested-mismatch row. |
| Medium | G29 | `AuthenticationConfiguration.cs:63,66` | Untested: certificate checks and HTTPS metadata stay on outside Development. A regression would accept any certificate on the OIDC backchannel in production. | JwtBearer options | 3 | No Http test inspects JwtBearerOptions (grep BackchannelHttpHandler, RequireHttpsMetadata: none). |
| Medium | G39 | `RunSchedulingEventRegistration.cs:110-111; SwitchService.cs:83` | Untested: a queued run start is consumed once, the sweep starts one left on an Active pipeline, and a sweep during a drain doesn't. A regression would start a new run every minute, or strand the queued run. | RunSweep.Sweep to OnSwitchCompleted | 3 | Startup_CompletesALeftoverDrain_AndStartsQueuedRun goes through TryCompleteDrain's own path and doesn't assert the queue emptied. |
| Medium | G88 | `ConfigActivationService.cs:58; ConfigValidation.cs:103` | Untested: the activation gate reads the instance's StepServiceAccount, for manifests with and without a select. A regression would activate a cluster-access step on an instance with no account, or hold every such version on one that has it. | ConfigActivationService.Check:58 to ConfigValidation.BranchNeeds | 3 | Gate_AsksForClusterAccess passes the flag directly, and only with a select. Grep StepServiceAccount in ConfigGateTests: none. |
| Medium | G89 | `ConfigActivationService.cs:87,99,168` | Untested: activation clears the pending marker, the explicit switch's success path, and a new pending version replacing an older one with the same reasons. A regression would leave a stale marker, or make Recheck activate the older version. | ConfigActivationService.Activate, Switch, MarkPending | 3 | ConfigGateTests:150 is the refused path; pending-then-activated tests go through Recheck. |
| Medium | G01 | `HarnessJobBuilder.cs:301-302` | Untested: map-trigger pods have no service-account token and no service links. A regression would give repo mapper code the runner namespace's default ServiceAccount token and service env vars. Impact depends on that account's RBAC: the repo's manifests bind nothing to it, and no namespace-level automount=false exists. | TriggerMappingService to HarnessJobFactory.CreateMapTriggerJobAsync | 2 | MapTriggerJob_RunsTheMapperInHarnessBase asserts image, command and secret keys only. The golden test covers step Jobs, not this builder. |
| Medium | G02 | `HarnessJobBuilder.cs:317` | Untested: configured StepCpuRequest, StepMemoryRequest and StepMemoryLimit reach the step container. A regression would silently drop them and step pods would run unbounded. | HarnessJobBuilder.Build:201 | 2 | No test sets these options (grep: production code only). |
| Medium | G07 | `ManifestValidator.cs:286` | Untested: a step in only some select cases is maybe-available. A regression would pass a required input from it at reconcile, and the consumer would be skipped at run time when the other case is selected. | CheckSelect | 2 | SelectTests' step is in both cases; no missing-from-one-case row. |
| Medium | G09 | `ManifestValidator.cs:365,646` | Untested: a $case-narrowed config ref outside any select is rejected, a step nested in two selects keeps its ref, and a narrowed ref in a failure handler gives an issue. A regression would accept the first, refuse the second, or throw on the third. | CheckStep to CheckConfigRefs | 2 | SelectTests 'leaked' only tests the wrong case inside one select. |
| Medium | G124 | `HarnessJobControl.cs:91,101` | Untested: the stuck grace accumulates and resets. A regression would leave an ImagePullBackOff Job until its deadline, or delete one that recovered. | HarnessJobControl.WaitAsync | 2 | Tests set FatalWaitingGrace to Zero (KubernetesJobExecutorTests:68), so the timer never matters. |
| Medium | G23 | `JsonSchemaValidator.cs:163,239` | Untested: defaults filled inside a union reach the result and the applied-defaults list. A regression would drop a oneOf member's default from run input, so when clauses and steps see it missing, and the changed-default warning would miss it. | CheckObject, CheckUnion | 2 | No test has a default inside anyOf/oneOf under a property. |
| Medium | G26 | `RunPersistenceService.cs:122; IPersistenceReadiness.cs:31` | Untested: /api/ready waits for every gate. A regression would report ready once config persistence is ready, and serve empty run state. | Health/InstanceHandlers.cs:35 | 2 | Tests use a readiness with this one gate: IsReady is false with no gates, so removal is invisible. |
| Medium | G38 | `RetentionPlanner.cs:149` | Untested: only keys under runs/ are run objects. A regression would count versions/<p>/<n>/manifest.json and step.tar.gz as objects of run <n> and delete them, and startup would then refuse to load (IndexListingMissingManifest). | Plan to RunObjects() | 2 | Binaries test's manifest.json has run #5 retained, so it never reaches the orphan path. |
| Medium | G48 | `AttemptFileRefs.cs:51; HarnessFileRefs.cs:90` | Untested: file refs inside arrays are found. A regression would give a step with a list of files no pre-signed URL for them. | AttemptLaunchService.cs:72 and HarnessJobFactory.cs:33 | 2 | FileRefs_AreDetectedByShape's list repeats the ref already at $.a, so de-dup hides the missing walk. |
| Medium | G72 | `WebhookTriggerService.cs:65,69` | Untested: the webhook secret cache expires and rechecks. A regression would keep verifying a rotated-out secret indefinitely, or refuse a new one until the TTL ends. | ReceiveAsync | 2 | No rotation or TTL test in WebhookTriggerTests. |
| Medium | G73 | `WebhookTriggerService.cs:94,105` | Untested: turned-away and redelivered webhooks release their in-flight slot. A regression would leak a slot each time until all webhooks answer Busy until restart. | ReceiveAsync | 2 | Redelivery tests check one delivery's outcome, never the counter afterwards. |
| Medium | G83 | `ConfigRefs.cs:28,34` | Untested: $config refs nested in objects and arrays are found. A regression would stop checking them, so a bad nested ref would pass reconcile and fail in the step. | ManifestValidator.cs:625,644 | 2 | ConfigRef_ToAField has nested refs but asserts valid, which holds if none is found; the negative tests use top-level refs. |
| Medium | G86 | `ManifestWriter.cs:152-153` | Untested: the canonical manifest writes a bearer verify. A regression would store a manifest that no longer parses, and every run of that pipeline would fail as 'no usable manifest'. | ManifestVersioning.cs:46 to PipelineReconcileService.cs:102 to RunPlan.cs:246 | 2 | Manifest_RoundTripsBothSchemes parses both but compares only the hmac trigger as written. |
| Medium | G98 | `YamlJson.cs:92,111` | Untested: the YAML depth guard. A regression would let a 256 KB document of nested brackets recurse until the stack overflows, killing the process. Needs an authenticated config save, and nothing is stored, so no crash loop. | ConfigSnapshotService.Add and ConfigService to YamlJson.Parse | 2 | Rejects rows have no deep document. |
| Medium | G06 | `ManifestValidator.cs:256` | Untested: the same step name twice in one select case, or across two selects, is a duplicate. A regression would let two steps share one identity, while history, scheduling and blocks are keyed on name. | RegisterStageStep | 1 | SelectTests only covers a name outside any case (empty case list). |
| Medium | G106 | `PushTriggerService.cs:37` | Untested: a polling binding with onPush gets a push poll source. A regression would remove it, so without a webhook pushes would never trigger. | PollScheduler to PushTriggerService.Sources to CheckAsync | 1 | DeployPollServiceTests:108 asserts only the empty case. |
| Medium | G116 | `YamlJson.cs:137` | Untested: plain false in config YAML is a boolean. A regression would make it the string "false": refused by a boolean schema, or treated as truthy by an untyped one. | YamlJson.Parse to ScalarValue | 1 | CoreSchema row has a: true and b: no, never false. |
| Medium | G119 | `AttemptLogStreamer.cs:66` | Untested: the live log stream waits between polls. A regression would make each open stream hammer the K8s API and storage in a tight loop. | Log stream handler to AttemptLogStreamer | 1 | AttemptLogStreamerTests has one test; it never observes the poll rate. |
| Medium | G130 | `KubernetesConfiguration.cs:16` | Untested: Kubernetes:RuntimeClassName reaches the options. A regression would silently run step pods on plain runc, without the gVisor sandbox. | WebApplicationBuilder startup to AddHarness to HarnessJobBuilder.cs:218 | 1 | StepJob_NoRuntimeClass is builder-level; grep RuntimeClassName in configuration tests: none. |
| Medium | G21 | `JsonSchemaValidator.cs:281` | Untested: the JSON Schema minimum keyword is enforced. A regression would accept out-of-range manual input or config and start a run. | Check to CheckNumber | 1 | Integers_Bounds_Arrays uses exclusiveMinimum and maximum, never minimum. 3902 is covered only by failure-context validation. |
| Medium | G27 | `RunPersistenceService.cs:512` | Untested: entity version history survives a restart. A regression would renumber step and model versions and mark changes wrongly. | LoadAsync | 1 | EntityVersions appears only in test wiring; no Restart_ test asserts it. |
| Medium | G31 | `JobRunner.cs:45` | Untested: a Waiting attempt is not dispatched. A regression would launch a gated step's Job before anyone answers, past the busy-key gate (it checks Scheduled only), so it could run beside an InProgress attempt of the same step. It would not bypass approval: the SDK's validateInput exits with 'input.<key>: missing' before the handler. | JobRunner.DispatchTick; JobExecutorBase.EnsureRunning; KubernetesJobExecutor.SubmitAsync. SDK: src/sdk/src/harness/validate.ts:96 | 1 | Only the MaxConcurrentJobs test covers it. No Job*Tests mentions Waiting; ExternalInputTests never runs JobRunner. JobExecutorBase, SubmitAsync and AttemptLaunchService don't refuse Waiting. SDK behaviour read in src/sdk/src/harness/validate.ts:96-122. |
| Medium | G33 | `RetentionPlanner.cs:97` | Untested: stale trigger events go and fresh ones stay. A regression would delete fresh events before their mapper Job reads them, and never sweep stale ones. | Plan | 1 | StaleTriggerEvents_Go asserts Count==1 with one old and one new event, not which one. |
| Medium | G35 | `RetentionPlanner.cs:239` | Untested: only unfinished step Jobs count as leaked. A regression would delete a build Job running past ClusterGrace (1h) mid-build, and finished step Jobs early. | Plan to Jobs() | 1 | Jobs_OnlyV2Roles has no unfinished non-step Job, and no finished step Job past the grace. |
| Medium | G36 | `RetentionPlanner.cs:167` | Untested: an orphan run waits until its newest object is past the grace. A regression would delete a directory still being written. | Plan to RunObjects() | 1 | OrphanedRunObjects uses same-age objects in each run. |
| Medium | G44 | `RunPlan.cs:168` | Untested: a when field with several allowed values matches any of them. A regression would match nothing: the step would be silently excluded and the run would Succeed without it. | PlanStage.EvaluateWhen, StageScheduler.Decide | 1 | Every when in the fixtures has one value; ManifestVersioningTests:187 only hashes a two-value when. |
| Medium | G47 | `RunCancellationService.cs:30` | Untested: a Running run with no active attempt (held at a blocked step) can be cancelled, and so can a finished run's leftover attempts. A regression would answer 'already Running', and the operator couldn't stop a held run. | Run cancel handler | 1 | No cancel test on a held run. |
| Medium | G54 | `JobObsoletionService.cs:48` | Untested: the re-check inside Update never obsoletes an attempt that just went InProgress. A regression would mark a running attempt Obsolete while its Job carries on. This is a race, so a test needs a seam. | HandleAttemptAdded | 1 | Attempt_AfterFirstInProgress covers the snapshot filter, not the race. |
| Medium | G55 | `KubernetesJobExecutor.cs:219` | Untested: a poll error on an InProgress attempt rethrows so the watcher reattaches. A regression would count it as a submission failure, and a transient K8s API error would fail a healthy attempt while its Job runs on. | JobExecutorBase watcher | 1 | Reattach_* tests have no error during the watch. |
| Medium | G59 | `FailureHandlerService.cs:106` | Untested: handlers fire only on Running to Failed. A regression would fire on-failure handlers on any update of a Running run, and on a run that recovered on retry. | RunEvents.OnUpdated | 1 | FailureHandlerTests has no retry-then-succeed run. |
| Medium | G63 | `PipelineSecretService.cs:239` | Untested: deleting a pipeline deletes its K8s Secret. A regression would leave its secret values in the cluster for good; retention only sweeps URL Secrets. | PipelineEvents.OnDeleted | 1 | DeleteAll_RemovesThePipelinesSecret calls the method directly; PipelineSecretEventRegistration is in no test. |
| Medium | G94 | `ConfigSnapshot.cs:47` | Untested: config snapshot numbers keep rising. A regression would give the third save the same number as the second, overwriting a write-once snapshot and breaking rollback. | ConfigService save to ConfigSnapshotService.Add | 1 | No test saves three snapshots. |
| Low | G15 | `ManifestParser.cs:86,267,288,478-481; ManifestValidator.cs:350,481-482,525,571; HarnessFailures.cs:116,131,161; JsonSchemaValidator.cs:99,319; ExternalInputService.cs:76,99; HarnessResultArchive.cs:112-113; YamlJson.cs:65,154; HarnessJobControl.cs:86,97; RunPlan.cs:123; RunContinuationService.cs:109; AttemptFileRefs.cs:51; KubernetesJobExecutor.cs:171; ConfigRefs.cs:33; GitHubConfigSource.cs:122; ApiResultMapping.cs:23; ProblemTypes.cs:54; HarnessAttemptResult.cs:79` | Untested: message text chosen by logic, not literal strings: which of two messages, hints, issue paths, order inside a message. A regression would show wrong or garbled text. Separate from the 763 string survivors; plan step 2.3's table covers most. Decline with no comment would throw (ExternalInputService.cs:76). |  | 48 | Rows assert codes; plan step 2.3's table would cover most of these. |
| Low | G53 | `RunViewService.cs:90,92,104,106-107,115; Attempt.cs:76,80,84; ManifestHandlers.cs:29,45; PushTriggerService.cs:56,69; RunPlan.cs:220; PipelineHealth.cs:53; PipelineSummaryService.cs:55; PipelineVersionService.cs:68; TriggerMappingService.cs:43; ConfigActivationService.cs:164; ConfigService.cs:148; ConfigValidation.cs:84; BindingHandlers.cs:195; PipelineReconcileService.cs:132` | Untested: read views: Held vs Pending, awaited inputs, phase seconds, health's held list, last activity, version order, run origin, effective-pipeline view, binding warnings, manifest-validate severities, reconcile report, changed-default warning. A regression would show an operator the wrong state; nothing acts on these. | API handlers | 38 | WaitingFor is asserted only unanswered (ExternalInputTests:175). Phases only with all timings set (KubernetesJobExecutorTests:157). ForSnapshot/ForCurrentConfig callers are VersionHandlers, PipelineSecretService and PipelineSummaryService, none on run start. |
| Low | G16 | `RetentionPlanner.cs:97,131,167,180,215,239,241; HarnessResultArchive.cs:42,62,69,103; AttemptService.cs:44,154,202; WebhookTriggerService.cs:65,67,69; JsonSchemaValidator.cs:281,283; YamlJson.cs:36,71; HarnessEnvelope.cs:38,66; StageScheduler.cs:62; ManifestValidator.cs:352; HarnessJobBuilder.cs:140; ManifestParser.cs:34; CriticalPathService.cs:41; AttemptLogStreamer.cs:58; HarnessJobControl.cs:92; HarnessResultCollector.cs:42` | Untested: exactly-at-the-limit cases, the same shape as the doc's RunStartService.cs:153 row. A regression would move a limit by one byte, entry or tick; retries: 0 is the only legal value that would be refused. |  | 31 | Tests stay well past each limit. |
| Low | G114 | `CriticalPathService.cs:32,35,38,41,46,65-68,75,93,99,111-112,115,126,153` | Untested: critical-path report numbers: range clamps, totals and shares, averages, stage membership, idle time, and empty cases (0/0 gives NaN, which JSON can't write). A regression would show wrong analysis, or a 500. | Critical path handler | 27 | CriticalPathTests has 8 single-scenario tests; nothing acts on the report. |
| Low | G24 | `JsonSchemaValidator.cs:101,202,219,229,260,281,283,285,287; JsonSchemaShapes.cs:33,52,93,95,117,159,161,171; ConfigSecretRefs.cs:36` | Untested: a malformed or unusual keyword value in an untrusted schema is ignored. A regression would throw on run start or at reconcile; a type array on a when field reaches this at JsonSchemaShapes.cs:95. | JsonSchemaValidator.Check; ManifestValidator.CheckWhen, CheckSelect | 18 | grep malformed enum, properties, required, pattern, minimum values: 0 hits. |
| Low | G111 | `ChangeEventRegistration.cs:31-32,35-36,39,43,48-50,56; PipelineConfigBindingEventRegistration.cs:13-15; PipelineEventRegistration.cs:12; ChangeEvents.cs:20; PipelineSecretCopyService.cs:56` | Untested: each change reaches the event stream. A regression would leave the UI stale for that change until a refresh. A polling read still sees the truth. | Hubs to ChangeEventRegistration to /api/events | 17 | ChangeEventRegistrationTests covers some subscriptions; these lines aren't asserted, and hub forwards are wired by tests themselves. |
| Low | G32 | `RetentionPlanner.cs:149,290,297-298,303,305` | Untested: foreign keys in the bucket are left alone and don't stop the sweep. A regression would make int.Parse throw on one odd key, stopping retention every interval. | Plan | 16 | All test keys are well-formed. |
| Low | G18 | `ManifestParser.cs:159,199,216,236,375,407,420,436,565` | Untested: wrong-typed fields give an issue, not an exception. A regression would fail reconcile with a raw exception (DeployPollService catches it), or a parse with zero issues (375). | ManifestParser.TryParse | 15 | ManifestParserTests rows don't cover these fields' types. |
| Low | G110 | `ApiErrorMiddleware.cs:22,32-33,89,95,102-103; ApiProblem.cs:99` | Untested: API error mapping edges: 500 vs 503 for HTTP, Socket and cancellation errors, 401/403 and bare 400 bodies, empty 2xx responses, exceptions after the response started, trace id. A regression would answer the wrong status, or put a problem body on a 204. | UseApiErrors, ApiErrorMiddleware.IsTransient | 13 | ApiErrorTests has one row per class; TraceId_MatchesTheLogScope passes with either source. |
| Low | G113 | `RetentionService.cs:155,157-158,168,181,197-200` | Untested: the dry-run log's verb, per-category counts and 'skipped, unavailable' suffix. This log is the only output of the default mode, so a regression would mislead an operator deciding to turn deletion on. | SweepAsync to LogItems, LogSummary | 13 | Tests use NullLogger, so nothing reads the log. |
| Low | G65 | `HarnessConfiguration.cs:16-18,25; ServiceConfiguration.cs:146-147; StorageConfiguration.cs:14,16; CliReleaseQuery.cs:25; GitHubEndpoints.cs:18` | Untested: configured values are honoured: Storage:Bucket, Storage:Mode, Links:BaseUrl, CLI step, Harness:* and GitHub URLs. A regression would silently use the default. | Startup configuration | 12 | grep Storage:Bucket, Storage:Mode: none. CliDistributionTests:77 asserts the default CliStep, never a set one. |
| Low | G99 | `YamlJson.cs:127,139,150` | Untested: explicit !!int, !!bool, !!float, !!null tags, .inf/.nan refusal, and invalid tagged values. A regression would refuse the tags or pass the values as strings. | YamlJson.ScalarValue | 10 | Rejects has only !custom; no .inf row. |
| Low | G128 | `HarnessSentinels.cs:38,53,58,63,68` | Untested: sentinel edges: markers must start a line, an empty block, a block starting with an olve-build line. A regression would let a log line that embeds a marker cut or forge a manifest or result block. | Build log and map-trigger log parsing | 7 | HarnessSentinelsTests cover begin-marker cases and truncation, not these. |
| Low | G66 | `PollHttp.cs:23,62-65` | Untested: the poll client ignores proxies, and the exact edges of private ranges. A regression could let a proxy env var skip the address check, count 100.127.x.x as public, or over-block public addresses. | PollTriggerService.cs:32, ExternalInputPollService.cs:29 | 7 | PollHttpTests rows are mid-range and only 8.8.8.8-style public. |
| Low | G101 | `GitHubConfigSource.cs:115,125,137-138; ConfigCursor.cs:121` | Untested: cursor edges: a path with no commits, a dependency new to the cursor, an empty ETag, root and unnormalized binding paths. A regression would throw or stop watching local dependencies. | GitHubConfigSource.GetAsync | 6 | ConfigCursorTests has 6 tests: no path with no commits, no empty ETag, no root or ./ binding fetch. |
| Low | G43 | `ExternalInputService.cs:72,107,117; CallbackInputs.cs:64` | Untested: an input listing several source kinds accepts each one, and a person can't answer a callback-only input by naming it. A regression would refuse mixed-source answers or skip the kind check. | ExternalInputService.Answer, CallbackInputUrls.ForRunAsync | 6 | No mixed-source input in tests. |
| Low | G49 | `AttemptFileRefs.cs:59; HarnessFileRefs.cs:114` | Untested: only exact file-ref shapes count. A regression would throw launching an attempt whose input has a near-miss object, such as a string run field. | AttemptFileRefs.Extract; HarnessFileRefs.Find via HarnessJobFactory.cs:33 | 5 | grep '"run":"': 0 hits. The test's {"run":1} is numeric. |
| Low | G62 | `PipelineSecretService.cs:179,208` | Untested: the secret list's description merge, and poll triggers listed as users. A regression would misreport them. | ListAsync | 5 | grep 'trigger:' in PipelineSecretServiceTests: 0 hits. Description is asserted only for one user (line 51). |
| Low | G10 | `ManifestValidator.cs:427,458; ExternalInputService.cs:50; RunInputService.cs:54` | Untested: mappings, answers and manual input use a model's input view. A regression would demand fields that have defaults. Loud. | ManifestValidator; ExternalInputService.Answer; RunInputService | 4 | The fixture's only input view is DeployRequest, the pipeline input, which no step uses. |
| Low | G52 | `PipelineSequences.cs:45,48; RunService.cs:186; PipelineVersionService.cs:89` | Untested: the number floor when sequences lag the store. A regression would repeat a number and overwrite run.json. Needs lost sequences. | RunService, PipelineVersionService | 4 | No lagging-sequence test. |
| Low | G04 | `HarnessJobBuilder.cs:240` | Untested: 'Invalid trigger name' for blank, whitespace and control characters. A regression would only make the mapper not find its trigger: Command is argv, so no injection. | HarnessJobFactory.cs:96 | 3 | Only a valid name is tested. |
| Low | G100 | `BindingHookStateStore.cs:28,36-37` | Untested: hook state changes reach persistence. A regression would lose a hook id on restart, and the service would register a duplicate on GitHub. | BindingHookPersistenceService.cs:47 | 3 | BindingHookRegistrationTests read the store, not the save. |
| Low | G104 | `BindingWebhookEventRegistration.cs:90; DeployPollService.cs:86` | Untested: a malformed repo string and a pipeline with no binding. A regression would throw instead of a warning or a 404. | BindingWebhookEventRegistration; ReconcileNowAsync | 3 | grep 'owner/name', 'has no config binding' in tests: 0 hits. |
| Low | G123 | `HarnessJobControl.cs:28,30,116` | Untested: Combined only adds the init log when the main log is empty, and the init log is fetched. A regression would pollute every attempt log, or hide the reason for an init failure. | GetLogsAsync callers | 3 | grep Combined, 'no output from container': none. |
| Low | G58 | `FailureHandlerPlanner.cs:135-137` | Untested: the context is projected to an older schema. A regression would skip an older pinned version's handler once FailureContext gains a field. | FailureHandlerPlanner | 3 | grep older, Project in FailureHandlerTests: 0 hits. |
| Low | G64 | `PipelineService.cs:23` | Untested: a manifest changing only the name or only the description is saved. A regression would drop the change. | PipelineReconcileService.cs:128 to SetDefinition | 3 | grep SetDefinition in tests: none. |
| Low | G84 | `ManifestVersioning.cs:96,139,144` | Untested: optional, clusterAccess and the right config entry feed the step hash. A regression would not bump the step version on those changes, or bump the wrong step. The version itself still changes. | ManifestVersioning.Compute | 3 | EachStepContentField has neither; ClusterAccess_RoundTrips_AndBumps... asserts text, never a version. |
| Low | G08 | `ManifestValidator.cs:301` | Untested: a select on a non-literal or non-string discriminator gives SelectInvalid. A regression would throw instead of naming the select; no bad manifest would be accepted. | CheckSelect:264 | 2 | git grep SelectInvalid in tests: nothing. |
| Low | G105 | `PollTriggerService.cs:174,254` | Untested: Prune with nothing persisted, and an array index one past the end in valuePath. A regression would throw instead of a no-op or a not-found problem. | PollTriggerService | 2 | PollTriggerStateTests:108 has persisted state. |
| Low | G112 | `ConfigEventRegistration.cs:10; PipelineConfigBindingEventRegistration.cs:17` | Untested: pipeline deletion cleans up its binding and config snapshots. A regression would keep polling a deleted pipeline, and leave its config. | PipelineEvents.OnDeleted | 2 | PipelineConfigBindingTests:31 wires the cleanup by hand. |
| Low | G127 | `HarnessResultCollector.cs:97` | Untested: the temp dir is deleted. A regression would fill the controller's disk over time. | HarnessResultCollector | 2 | grep tempDir: none. |
| Low | G129 | `KubernetesClient.cs:53; KubernetesConfiguration.cs:25` | Untested: Kubernetes client auth wiring. A regression would send no token, or skip the in-cluster client. Loud at start; needs a real-system check. | KubernetesConfiguration.ConfigureInCluster | 2 | grep AuthMode in tests: none. |
| Low | G14 | `ManifestValidator.cs:577; JsonSchemaShapes.cs:153` | Untested: a ref with an unknown shape or type on one side is accepted leniently. A regression would throw during reconcile. | CheckShape, CheckMapping | 2 | No any-typed source or target row. |
| Low | G19 | `ManifestParser.cs:399; ManifestWriter.cs:254` | Untested: an external input's description is parsed and written. A regression would blank it in the UI and the step hash. | Writer via PipelineReconcileService.cs:102 | 2 | grep Description in tests: step and secret descriptions only. |
| Low | G20 | `ManifestParser.cs:411,413` | Untested: unknown properties on person and callback sources are refused. A regression would accept them silently. | ParseExternalSource | 2 | grep ['kind'] with an extra property: none; ExternalInputTests:169 is a valid source. |
| Low | G46 | `RunContinuationService.cs:48; AttemptService.cs:121` | Untested: active-attempt refusal with mixed attempts. A regression would refuse a never-attempted step, or allow a continue while one attempt is active. | RunContinuationService.cs:91 | 2 | No mixed-attempt row. |
| Low | G56 | `KubernetesJobExecutor.cs:256,268` | Untested: submission retries wait between tries, and keep earlier log lines. A regression would spend all retries at once in an outage, or drop the log. | HandleSubmissionFailureAsync | 2 | Tests set SubmissionRetryDelay = Zero (KubernetesJobExecutorTests:82), so the wait is invisible; log text unasserted. |
| Low | G61 | `PipelineSecretService.cs:98,223` | Untested: SetAsync's happy path. A regression would refuse every secret set; the real path is checked by beta-test at most. | Secret handlers | 2 | Only line 97 calls SetAsync, with kubernetes:false. |
| Low | G69 | `BuildJobBuilder.cs:118` | Untested: backslash and NUL in the binding path are refused. A regression would accept them into the build Job. | BuildJobBuilder.Build | 2 | grep backslash, NUL in BuildJobBuilderTests: 0 hits. |
| Low | G85 | `CanonicalJson.cs:114-115` | Untested: canonical key order for key "0", the empty key, and long digit keys. A regression would change the hash against describe's bytes, or throw. | ManifestVersioning | 2 | CanonicalJsonTests row has 9, 10, 01 and the uint edges, not these. |
| Low | G03 | `HarnessJobBuilder.cs:251` | Untested: map-trigger Job labels. A regression would lose ops selection, and PodWatchService's app=olve-pipelines watch, so the wait falls back to polling. Retention isn't affected: these Jobs have a TTL. | TriggerMappingService; PodChangeSignals.cs:89 | 1 | MapTriggerJob test doesn't assert labels. |
| Low | G107 | `WebhookTriggerService.cs:167` | Untested: a delivery that failed to map is forgotten, and a good one isn't. A regression would ignore the sender's redelivery of a failed delivery. | MapAsync | 1 | Redelivery_OfADeliveryThatStartedNothing stops at the StaleConfig gate (WebhookTriggerTests:178), before the mapper outcome. |
| Low | G108 | `PollScheduler.cs:91` | Untested: a source running past its interval isn't started again. A regression would overlap it; the scheduling and binding locks limit the damage. | PollScheduler tick | 1 | RunningSource_IsNotStartedAgain stays inside the interval, where nextAt also blocks it. |
| Low | G109 | `ConfigSnapshot.cs:58` | Untested: TryGetLatest's result. A regression would make config GET answer empty, so an edit could start from a blank document. | ConfigService.cs:74, RunPlan.cs:223 | 1 | Callers at :57 and RunStartService:136 ignore the return, so tests pass. |
| Low | G121 | `HarnessFailures.cs:119` | Untested: an OOM-killed init container is classed OutOfMemory. A regression would report InitFailed. | HarnessExitCodes.Classify | 1 | grep OOMKilled: 1 hit, OomKilledReason_Wins, on the main container. |
| Low | G131 | `PipelineSecretCopyService.cs:71` | Untested: an empty names list copies all secrets. A regression would copy nothing. | Copy secrets handler | 1 | CopyAll_WhenNoNames passes null names, never an empty array. |
| Low | G22 | `JsonSchemaValidator.cs:294` | Untested: type arrays match by element. A regression would pass any value. zod emits anyOf for nullables, so reach is small. | Check | 1 | grep '"type":[' and JsonArray type: 0 hits. |
| Low | G37 | `RetentionPlanner.cs:131` | Untested: age counts from the finish. A regression would expire an old run continued recently, early. | SelectExpiredRuns | 1 | Test runs don't separate the two. |
| Low | G50 | `ExternalInputPollService.cs:96` | Untested: a null at the poll path means no answer yet. A regression would treat null as a value. | ExternalInputPollService | 1 | Poll test (ExternalInputTests:200) serves no JSON null at the path. |
| Low | G51 | `ActiveAttemptQuery.cs:24` | Untested: the waiting-only filter of /api/attempts. A regression would return the wrong list. | Attempts list handler | 1 | Only the default list is tested. |
| Low | G57 | `FailureHandlerPlanner.cs:59` | Untested: no failed attempts means no handler dispatch. A regression would fire handlers for a run failed by the scheduler. | FailureHandlerService.HandleRunFailed | 1 | grep empty failedAttempts: 0 hits. |
| Low | G67 | `HarnessResultArchive.cs:38` | Untested: entries are streamed. A regression to copyData:true would buffer each in memory, and a big artifact could OOM the controller. Coverage can't see this. | HarnessResultCollector | 1 | grep copyData: 0 hits. Oversize tests use small limits. |
| Low | G68 | `HarnessResultArchive.cs:62` | Untested: a zero-byte file in a result archive is fine. A regression would fail the attempt with 'contents exceed'. | HarnessResultCollector | 1 | Archive tests use non-empty entries (F(...), new byte[4096]); no zero-length FileEntry. |
| Low | G75 | `TriggerMappingService.cs:105` | Untested: a mapper with exit 0 and no result is a failure. A regression would silently drop the event as a skip. | MapAndStartAsync | 1 | TriggerMappingServiceTests has Skipped and Failed mappers; none exits 0 with no result block. |
| Low | G77 | `BuildJobBuilder.cs:149` | Untested: the binding's credentials key is used for the clone. A regression would use the default key and fail a private repo's build. | PipelineReconcileService build Job | 1 | grep TokenSecretKey in tests: none. |
| Low | G78 | `TriggerMappingService.cs:81` | Untested: the event is uploaded before the mapper Job. A regression would fail every automatic trigger. Loud; beta-test would catch it. | MapAndStartAsync | 1 | TriggerMappingServiceTests:48 asserts no triggers/ object remains, which passes if none was written. |
| Low | G81 | `JsonSchemaShapes.cs:66` | Untested: local $ref is followed. A regression would refuse refs through reused or recursive schemas. Loud. | All JsonSchemaShapes entry points; also the G12 guard's input | 1 | No manifest test uses $ref (grep); the only $ref test is JsonSchemaValidator rejecting it. |
| Low | G97 | `ConfigValidation.cs:173` | Untested: a config step in a run with no config gets an empty slice. A regression would throw at launch. | AttemptLaunchService | 1 | ConfigValidationTests:102 always passes a config; no null run config. |

G103, G120 and G129 concern real-system behaviour that fakes can't show, so they belong to step 5.

## Does each test earn its place?

| Measure | Tests |
|---|---|
| Test cases at runtime (627 methods) | 869 |
| At least one unique kill | 364 |
| Kills, but none unique | 463 |
| No kills | 42 |

A greedy selection keeps all 3,988 kills with about 416 tests. **That is not a removal target.** Unique kills miss rule documentation, security guards and races, which is why the 463 are candidates and not deletions.

### Identical kill sets

48 groups cover 121 test cases. 32 groups are rows of one parameterized test, such as `EachCheck_RejectsItsMutation`, and are not duplicates. Of the 16 cross-method groups, covering 43 cases:

- **Merge:** `JobRunnerTests.Job_FinishSuccess_TransitionsToSucceeded` and `SecondStepJob_FinishSuccess_TransitionsToSucceeded`, which differ only by step name.
- **Optional merge:** `PipelineSummaryServiceTests.UnboundPipeline_HasNullRepo` and `PipelineWithoutVersion_HasNoStages`.
- **Keep:** the remaining 14 state distinct rules or guards, such as the webhook signature tests, `Create_Concurrently_YieldsDistinctNumbers`, and `Conflict_Is409` with `Unavailable_Is503`.

### Zero kills (42)

- **12 `EntityStore*Tests`:** `EntityStore` lives in the Olve.Utilities package. Move them upstream unless they pin behaviour this repo depends on.
- **3 `FakeConfigSourceTests`:** they test a test fake. Fold them into the fake contract tests.
- **12 on production code with no recorded coverage:** keep all 12. None reaches a mutant that could detect it, and coverage capture is not at fault.

| Test | Why it has no kills |
|---|---|
| `PipelineHealthTests.Compute` (6) | All 4 mutants on the code fail with `CS8510`: flipping a tuple pattern makes a later arm unreachable. Safe Mode is not involved. |
| `BuildJobBuilderTests.Script_*` (2) | They read a `const string`, which Stryker never mutates. They are the only guard on the build script text. |
| `ManifestParserTests.RefMapping_SplitsRootAndPath` | No mutator applies to `Split('.')[0]` or `[1..]` |
| `CliDistributionTests.InstallScripts_*` | No mutant on the lines it reaches |
| `CliDistributionTests.OnlyAllowListedAssetsResolve`, 2 rows | Static set, and mutators can only shrink it, while these rows check it isn't wider. A security guard. |
- **15 covered but killing nothing:** mostly rejection rows and failure paths. Keep `BindingResponses_NeverCarryTheWebhookSecret` as a security guard. Review the rest for weak assertions.

### Weak assertion patterns

- **Bare `.Failed()`:** `HarnessResultArchiveTests.Oversize_Envelope_IsRejected` passes on any failure, so it can't show the size limit was the cause. Rejection tests should assert the problem code or type.
- **`.Failed).IsTrue()` or `.Succeeded).IsTrue()`:** 22 uses in 10 files, against the repo's Olve.Results.TUnit convention. 9 are in `PipelineConfigBindingTests`.

## Reliability and parallelism

| Phase | Runs | Failed runs | Median |
|---|---:|---:|---:|
| Scaling, contaminated timing | 21 | 0 | not used |
| Idle | 30 | 0 | 8.05s |
| 32 CPU burners on 16 cores | 30 | 0 | 16.63s |
| 4 copies at once | 40 | 0 | 7.97s |
| Scaling re-measure | 35 | 0 | see below |

That is 156 runs with no failed test, and every case passed in the 121 runs with per-test reports. A separate session logged 37 more clean runs, with counts only.

**What this does and doesn't show:** zero failures in 156 runs puts the suite's failure rate below about 2% at 95% confidence, which is inside the 5% budget. It can't identify a single test that fails 1 in 300 times, and it reflects this machine only. TUnit 1.58.0 has no shuffle and sorts by priority, then class name. `tools/test-audit/order-isolation.sh` removes a test's usual predecessors instead, running each class alone, each method alone and random half-sets of classes. It hasn't run yet, because it is CPU-heavy and a timing measurement was in progress. A true reversal needs a test code change. A scan found no mutable shared test state, so few hits are expected. Four simultaneous copies passing rules out most shared-port and shared-file collisions.

Parallel scaling, 5 runs each. Other work may have been running on the machine during this, so read the shape and not the exact figures.

| Max parallel tests | Median wall time |
|---:|---:|
| 1 | 11.46s |
| 2 | 8.34s |
| 4 | 8.26s |
| 8 | 6.15s |
| 16 | 6.86s |
| 32 | 5.39s |
| auto | 4.90s |

The suite gains only about 2.3x from full parallelism, using about 1.6 cores on average, because it mostly waits. `EndToEnd_ThroughJobRunnerAndNoOpExecutor` alone takes about 3 seconds, which sets a floor. 116 tests of 100ms or more account for 37.5s of the 50.5s total test time in idle runs.

Serial tests: one class, `LoggingJsonFormatTests` (`[NotInParallel("console-capture")]`), because it swaps the process-wide `Console.Out`.

Real time in tests:

- **Proving a negative after a fixed wait:** `JobRunnerTests.cs:185` and `:272`. These can pass falsely. Also review `JobRunnerTests.cs:200` and `JobDispatchSignalTests.cs:28`.
- **Polling at 10ms:** `JobRunnerTests.cs:75` and `:87`, `KubernetesJobExecutorTests.cs:97`, `SchedulerRuntimeTests.cs:232`.

## Outside this audit

- **Real systems:** no tracked test project runs against real Kubernetes, Garage, GitHub or Dex. About 20 hand-written test doubles carry those assumptions, and `beta-test` is the main real check.
- **Frontend:** `src/frontend` has no test files or test script.
- **TypeScript:** `src/sdk`, `src/cli`, `.pipelines/tests` and `src/deploy` hold 27 test files and 304 tests. They were audited by reading and not mutation tested. See [TypeScript tests](#typescript-tests).
- **Bug types mutation testing can't see:** a skim of the 70 most recent of 114 fix commits, by subject only, suggests external-interface and ordering bugs are prominent. A good mutation score does not protect against those.
- **Pipeline:** it already follows the no-repeat rule. `test` appears once, and `betaTest` sits in alternative `select` cases.
- **Olve.AgentRuntimeManager:** its suite is load-sensitive, unlike this one. In `Olve.AgentRuntimeManager#28`, 12 timing tests failed at a load average of about 50 and passed on rerun under similar load. They are not fixed. `Call_ThatStopsWithTheServer_StaysInterrupted` no longer depends on a timer (`8e762f5`).

## TypeScript tests

These are good tests overall: flow-shaped, exact about messages and argv, with almost no weak assertions.

| Finding | Location | Risk |
|---|---|---|
| The public-API guard can never fail: its regex needs double quotes and all 44 relative imports use single quotes. The rule holds today. | `src/sdk/tests/runtime/library-public.test.ts:29-31` | High |
| 13 tests run in no pipeline step or `tools/check.sh` | `.pipelines/tests` (10), `src/deploy/compose/deployer` (3) | High |
| The rollback path has no test | `deployer.ts:102-147` | High |
| A fixed 500ms sleep before SIGTERM | `build.test.ts:385` | Medium |
| Fake API bodies are untyped, so a renamed field leaves CLI tests green | `wait.test.ts:17-41`, and `run`, `pipeline`, `step` | Medium |
| Login, and most binding commands, are untested | `commands/login.ts` 0%, `auth/oidc.ts` 47%, `commands/binding.ts` 29% | Medium |
| `compile-fail` runs twice in one step | `src/sdk/package.json:53`, `tools/check.sh:97` | Low |
| Module state is set and not restored, which blocks `test.concurrent` | `wait.test.ts:5-7`, `publish-sdk.test.ts:39`, `deployer.test.ts:4-6`, `config-refs.test.ts:232,281` | Low |
| Upper bounds on wall time, and real timeouts in place of the `sleep` seam | `ctx.test.ts:116,150,161`, `wait.test.ts:200` | Low |

**StrykerJS and bun:** no official bun runner exists, and the community one needs core ^9 and has a report of bun incompatibility. The `command` runner works but reruns the suite for every mutant. That is an estimated 5 minutes for `src/cli` and 3 to 5 hours for all of `src/sdk`, so mutate the SDK one directory at a time. These are estimates; no run has happened.

## Findings from the new test tiers

These came from running against real systems, on branches that aren't on `main`. All are behaviours the unit suite couldn't show.

| Bug | Found by | How often | Status |
|---|---|---|---|
| Deleting a pipeline leaves its Jobs until the 7-day TTL | Real-system flow test | Every run | Fix in progress |
| Two concurrent first secret sets lose a value | Real-system test | 6 of 6 | Fix in progress |
| The UI shows nothing when adding a repo is refused | Browser test | 8 of 8 | Fix in progress |
| `POST /api/pipelines` accepts a repo that isn't `owner/name` | Browser test work | Seen directly | Fix in progress |
| An acknowledged run or `--cancel-running` switch is lost on SIGKILL, because run state flushes on a 1s timer | Chaos | Lost run 14 of 14 on two chosen seeds, lost switch 3 of 3 on one, 0 under graceful stop | Open |
| The result archive is deleted before the terminal status is durable, at `KubernetesJobExecutor.cs:158`, so a succeeded step can come back failed after SIGKILL | Chaos | 5 of 24 sweep seeds across the server-kill failures | Open |
| A restart right after a `--cancel-running` switch leaves the cancelled attempt's Job running, including on the SIGTERM a self-deploy sends | Chaos | 3 of 12 sweep seeds | Open |

Per-seed rates are replays of seeds chosen because they failed, so only the sweep figures are unbiased. The mechanism for the third chaos bug comes from reading the code. The Garage outage, Kubernetes outage and pod deletion families found no bug, but `docker pause` freezes all of k3s rather than only its API, and the Garage "will retry" path was never observed.

Unconfirmed: `FailureTarget` appears 31s before `Failed` on a deadline Job and the code reads only `Failed`, so a poll in that window could classify a timeout by exit code.

**Fakes that disagree with the real system:** 9 cases, each a skipped test with its reason. `FakeSecrets` create overwrites where the real one answers 409 and keeps the old data. `FakeSecrets` update of a missing Secret creates it where the real one throws. `FakeKubernetes.CreateJobAsync` always returns true where the real client returns false on 409, which is how the executor reattaches. `FakeS3` reports `LastModified` as the epoch. Hand-written `HarnessOutcomeTests` pairs DeadlineExceeded with a pod that exited 143, while the real pod list was empty.

No fixture in the repo was a true recording before this work. 13 are now recorded for GitHub, Dex and k3s, and they close G103 and G120. 10 hand-written fixtures are listed in `tools/recordings/hand-written.json`.

## Change plan

Proposals only, in priority order.

### 1. Close the dangerous gaps

Add tests for the High and Medium rows under Confirmed gaps, as in-process flow tests where possible. Add in-process flows for the reconcile build path, the binding API and step file serving. Take the 8 High rows from the survivor triage next, starting with token validation, the allow-list and per-change persistence, then the Medium rows.

### 2. Tighten assertions and timing

1. Assert the problem code or type in rejection tests, then re-check the 15 covered tests with no kills.
2. Replace the 22 `.IsTrue()` result assertions with `.Failed()` and `.Succeeded()`.
3. Add one table-driven test of issue code to exact message for manifest messages, then extend to other user-facing errors. Rewording a message means updating the table.
4. Replace fixed waits and polling with `TimeProvider` or explicit signals, starting with `JobRunnerTests.cs:185` and `:272`. Then remove the 3-second floor in `EndToEnd_ThroughJobRunnerAndNoOpExecutor`.
5. Make `LoggingJsonFormatTests` parallel by giving the formatter a writer instead of swapping `Console.Out`.

### 3. Tidy the suite

1. Merge the `JobRunnerTests` pair, and optionally the `PipelineSummaryServiceTests` pair.
2. Move the `EntityStore*Tests` upstream if nothing here relies on them.
3. Fold `FakeConfigSourceTests` into the contract tests.

Don't remove tests just for lacking a unique kill.

### 4. Measure the unmeasured

`ignore-mutations`, `ignore-methods` and `mutate` spans apply after rollback, so they can't help. Only `mutation-level` and `// Stryker disable` comments act before placement.

1. Run a compile-only pass for the real tally: `dotnet stryker -L --diag -O <dir> -m '**/*.cs{0..1}' -m '!**/*.g.cs' -r json`. Mutants still compile and roll back, then every mutant is filtered out, so this takes minutes.
2. Repeat with `-l Basic` to confirm the wipes stop.
3. Recover the mutants. `-l Basic` needs no source change but measures only about 135 of the 526 mutants in wiped members. `tools/test-audit/apply-stryker-comments.py` disables the triggers in a throwaway worktree and is expected to measure about 375. Neither changes `main`.
4. Narrow `ignore-methods` to `LogTrace`, `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical`, `LogProblems`, `LogSummary`, `LogItems`, then expect new survivors around `LogIfFailed`.

Re-run Stryker on each changed area, not on every build.

### 5. Real-system tests

| Requirement | Decision |
|---|---|
| Default | One host and no configuration: `localhost:5000` |
| Pool | Opt in by listing hosts in `TEST_TARGETS` or a config file. Adding hosts never changes a test. |
| Size | About 3 deployments, each with a different configuration where possible |
| Test code | Tests ask for a `TestServer`. Pooling stays inside it. |
| Hosts | Created and started outside the tests |
| State | Each test creates uniquely named pipelines through the API and asserts only on what it owns |
| Multi-step flows | One test per flow, with no dependencies between tests |
| Cleanup | Tests never clean up. Flows must work on an empty or full host. |
| Growth | Retention with short thresholds on test hosts, plus a prefix-based age sweeper as a fallback |
| Server-wide or disruptive tests | Marked `[ExclusiveServer]`, and kept rare. Other tests wait or use another host. |
| After an exclusive test | The host is reset before reuse. If that fails, it leaves the pool, or on a single host the run fails clearly. |
| Empty state | A small exclusive group runs against a freshly reset host |
| Deletion | A flow test deletes its pipeline and checks its Jobs, Secrets and objects are gone |
| Aged hosts | Test output records roughly how much data the host held, so scaling bugs aren't mistaken for flakes |

Order: Kubernetes first, then Garage, then the fake contract suites. Each runs the same assertions against the fake and the real system.

### 6. Recorded fixtures

1. Store capture date, source and system version with each recording, starting with GitHub and Dex.
2. Show expired fixtures as a one-line summary at the end of the test run. Expiry never fails a build.
3. Upgrading k3s or Dex marks those fixtures stale immediately.
4. Give GitHub fixtures a date-based expiry of 30 to 90 days.
5. Add a manual re-record command that captures, scrubs secrets and shows a diff for approval. A schedule can call it later.

### 7. Frontend flows

Add a small set of browser-level tests of the main user flows, running against a `TestServer` host. Choose the flows with the same earn-your-place rules.

### 8. Pipeline

- Run the new tiers once each, against the deployment or configuration they target.
- Keep special cases such as load tests off-cadence.
- Keep flake data from real pipeline runs, because local runs can't see rates near 1 in 300, and report the suite's combined rate against the 5% budget. The roadmap's step reliability tracking would supply this per step. Naming the individual flaky test still needs per-test results from the `test` step.
- First unexplained case for that data: `build-sdk` in `Olve.Pipelines v2#167` was killed with exit 137 and `ContainerStatusUnknown`, cause not established. `#168` passed.

### 9. TypeScript tests

1. Fix `library-public.test.ts`, and run `.pipelines/tests` and `deployer.test.ts` in a pipeline step and in `tools/check.sh`.
2. Remove the second `compile-fail` run, and replace `build.test.ts:385` with a readiness signal.
3. Add a `deploy()` rollback test against a fake Docker socket, and type the CLI's fake API bodies.
4. Try the `command` runner on `src/cli`, then the SDK a directory at a time.

### Metrics against the plan

| Metric | Result | Addressed? | How |
|---|---|---|---|
| Backend mutation score | 55.06% | Yes | Steps 1 and 2 raise it where it matters. The target is 80%+ on critical logic, not everywhere. |
| High-risk gaps (drain, retention guard, superseded attempts) | 3 confirmed | Yes | Step 1 |
| Medium and low gaps | 5 confirmed | Partly | Step 1 covers Medium. The 3 Low rows wait. |
| Gaps from the survivor triage | 116 rows: 8 High, 50 Medium, 58 Low | Partly | Step 1 covers High, then Medium. Low rows wait, and many fall to step 2. |
| Reconcile build path, binding API, file serving | 24.3%, 0%, 33.3% | Yes | Step 1 |
| String survivors | 763 | Yes | Step 2, table-driven message test |
| Other survivors | 1,061 | Yes | All triaged: 581 gap, 14 already listed, 124 equivalent, 340 harmless, 2 artefacts |
| Not-covered mutants | 1,441 | Partly | Steps 1, 5 and 7. Wiring is left to indirect coverage. |
| `Configuration` score | 31.7% | By design | Mostly metadata and wiring. `RequireRead()` is checked and is not a gap. |
| Unmeasured mutants | 1,876 labelled, about 845 (7.9%) real | Partly | Step 4 has the workaround. Its runs are still to do. |
| Mutants hidden by `*Log*` | About 60 | Yes | Step 4 narrows the pattern |
| Cross-method duplicate groups | 16 (1 merge, 1 optional) | Yes | Step 3 |
| No unique kill | 463 tests | No action, by design | Most keep their place as rules or guards |
| Zero-kill tests | 42 | Yes | Steps 2 and 3. The 12 with no recorded coverage are resolved and all stay. |
| Weak rejection assertions | Pattern found, not counted | Yes | Step 2 |
| `.IsTrue()` convention breaks | 22 in 10 files | Yes | Step 2 |
| Flaky runs | 0 of 156 | Yes | Step 8 tracks real pipeline data |
| Rare flakes near 1 in 300 | Can't be seen locally | Yes | Step 8 |
| Test order dependence | Not tested | Partly | `order-isolation.sh` is ready and has not run. A true reversal needs a test code change. |
| Serial test classes | 1 | Yes | Step 2 |
| Real-time waits | 4 fixed, 4 polling | Yes | Step 2 |
| Parallel speed-up | About 2.3x | Yes | Step 2 removes waits and the 3s floor |
| Suite run time | 4.9s | Protected | Budget of about 30s |
| Real-system tests | 61 on `track-f` (60 pass, 1 red for the delete bug), plus 5 off-cadence chaos test methods on `track-e`. Not in a pipeline. | Yes | Step 5 |
| Fakes unchecked against reality | About 20 | Partly | Kubernetes and S3 fakes now run a shared contract, with 9 known disagreements. Other fakes wait. |
| Frontend tests | 5 browser flows on `track-g` (4 pass, 1 red for the refused-repo bug). Not in a pipeline. | Yes | Step 7 |
| TypeScript tests | 27 files, 304 tests, audited by reading | Yes | Step 9. Mutation run still to do. |
| TypeScript tests in no pipeline step | 13 | Yes | Step 9 |
| Interface bugs in fix history | Prominent in a skim | Yes | Steps 5 and 6, though nothing measures it yet |

### Suggested targets

| Measure | Target |
|---|---|
| Critical logic mutation score (scheduling, validation, retention) | 80%+ |
| Unit and in-process flow suite | Under about 30s |
| Suite flake rate | Under 5%, combined |
| `[NotInParallel]` classes | 0 |
| Real-system tests | A few minutes, parallel across the pool |

## Not done

Open work from the pipeline package migration and the `pl wait` changes.

**Tests and reliability**

- Fix the 12 load-sensitive tests in Olve.AgentRuntimeManager: replace real-time waits with its clock seam and explicit signals. Not started. No retries.
- Find the cause of the `build-sdk` exit 137 in `Olve.Pipelines v2#167`.
- Try the `pl wait` rerun following and the held exit code 4 against a real rerun and a real held run. Only unit tests cover them.

**This plan**

- Track A is merged (`4bfd147`), but its gaps still read as open under Confirmed gaps, the change plan and the metrics table.
- `track-fixes` has commits for the bugs under Findings from the new test tiers, and the table has not been checked against them. They are not on `main`.
- `track-b`, `track-c`, `track-e`, `track-f`, `track-g`, `track-i` and `track-fixes` are unmerged, and their status has not been reviewed here.

**Pipelines and SDK**

- `packResult` writes a tar and then gzips it, which doubles scratch use for large outputs. This contributed to `claude-code` exceeding its 1 GiB limit.
- crafty's `deploy` and `verify` stay blocked and `crafty#2` was cancelled, so its migrated pipeline has not deployed. Unblocking is a prod action for Oliver to decide.

**From the roadmap, unchanged**

- npm staged publishing, before `NPM_TOKEN` expires in late January 2027.
- The `contentTag` cache key must cover build options.
- Pin `KANIKO_IMAGE`.
- Step reliability tracking.

**For Oliver**

- Pull the local checkouts of the migrated repos. They are behind origin. `Olve.Homelab`, `Olve.SlimeRepublics`, `Olve.Trains` and `QuestionBank` also have uncommitted work that was left untouched.
