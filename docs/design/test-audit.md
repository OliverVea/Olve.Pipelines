# Test suite audit and change plan

Audit of the backend unit tests at commit `143f9e8` (2026-10-31), with a proposed plan. This is analysis only: no code or tests were changed.

Reproduce from `src/backend/Olve.Pipelines.UnitTests`, after `cd src/spec && bun install && bun run build`:

```bash
dotnet stryker                          # full run, about 18 minutes
dotnet stryker -f stryker-control.json  # coverage-off control, about 7 minutes
```

## Verdict

- **Duplicates are not the main problem.** Of 16 groups of different test methods with identical kill sets, one is a clear merge and one is optional. The other 14 state distinct rules or guards.
- **Gaps are the main problem.** The backend mutation score is 55.06%, and some dangerous behaviour is untested, including the drain guard and the guard against deleting a run that is not finished.
- **The suite is reliable and already parallel.** No test failed in 156 runs, including under CPU saturation and with four copies running at once.
- **The biggest blind spots are outside this suite.** Real-system behaviour and the frontend have no tests of their own, and those are the likeliest sources of the interface bugs seen in the fix history.

## Standards applied

The rules are in `~/.claude/CLAUDE.md`. Decisions specific to this audit:

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

Files with the most undetected mutants worth attention: `ManifestValidator.cs` (232, 54.1%), `ManifestParser.cs` (185, 71.1%), `JsonSchemaValidator.cs` (108, 58.3%), `RunPersistenceService.cs` (89, 52.9%), `HarnessJobBuilder.cs` (72, 54.4%), `RetentionService.cs` (65, 19.8%).

763 survivors are string mutations. Under the decision to assert messages most are real gaps, though some strings are paths or other non-message values. The other 1,061 survivors are logic changes and need case-by-case judgement.

### How far to trust these numbers

- **Repeatability:** Manifests ran twice. 2,150 of 2,158 statuses matched, and 1,414 of 1,415 killer sets. All 8 status differences went from Survived to Killed. The one killer-set difference was `SchedulerRuntimeTests.EndToEnd_ThroughJobRunnerAndNoOpExecutor`.
- **Coverage-off control:** every test ran against 367 mutants from 6 files. 364 statuses agreed in substance (NoCoverage reads as Survived when coverage is off). The 3 differences all under-counted detection, so the score may be very slightly low. 68 of 69 killer sets matched.
- **Compile failures:** 1,876 mutants (17.5%) are unmeasured. Stryker's Safe Mode removes every mutant in a method when one fails to compile, and this fired 24 times. Sampled errors are `CS0165` (unassigned local) and `CS8070`. The `CS0165` link to `TryPickProblems(out var problems)` comes from samples, not a full tally. Affected methods include `ApplyStageOutcome`, `CheckWhen`, `CheckTriggers` and `MatchesDiscriminators`. `StageScheduler.cs` has 71 unmeasured against 91 measured, so the `Pipelines/Scheduling` score overstates what is known.
- **Runner:** the MTP runner is in preview. The checks above are why these results are treated as sound for this suite.

## Confirmed gaps

Each survived or was uncovered, and a search found no test for the behaviour.

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

Assessed as harmless or probably equivalent: `StageScheduler.cs:300` (guards a log line), `StageScheduler.cs:201` (looks equivalent, not proven), `StageScheduler.cs:298` (rewrites the same value). Not yet assessed: `StageScheduler.cs:49`, `ManifestValidator.cs:301`.

The remaining survivors need the same triage, starting with `ManifestValidator`, `RetentionService` and `HarnessJobBuilder`.

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
- **12 on production code with no recorded coverage:** `PipelineHealthTests.Compute` (6), `CliDistributionTests` (3), `BuildJobBuilderTests.Script_*` (2), `ManifestParserTests.RefMapping_SplitsRootAndPath`. These are not shown to be useless. `PipelineHealth` has 12 compile failures, so Safe Mode is the likely cause there, and the others are undetermined.
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

**What this does and doesn't show:** zero failures in 156 runs puts the suite's failure rate below about 2% at 95% confidence, which is inside the 5% budget. It can't identify a single test that fails 1 in 300 times, and it reflects this machine only. The test binary has no order-randomization option, so order dependence wasn't tested. Four simultaneous copies passing rules out most shared-port and shared-file collisions.

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
- **TypeScript:** `src/sdk`, `src/cli`, `.pipelines/tests` and `src/deploy` hold 27 test files that were not mutation tested.
- **Bug types mutation testing can't see:** a skim of the 70 most recent of 114 fix commits, by subject only, suggests external-interface and ordering bugs are prominent. A good mutation score does not protect against those.
- **Pipeline:** it already follows the no-repeat rule. `test` appears once, and `betaTest` sits in alternative `select` cases.

## Change plan

Proposals only, in priority order.

### 1. Close the dangerous gaps

Add tests for the High and Medium rows under Confirmed gaps, as in-process flow tests where possible.

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

Find out how to restructure, exclude or work around the Safe Mode cases, so `StageScheduler` and `ManifestValidator` get real scores. Re-run Stryker on each changed area, not on every build.

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
- Keep flake data from real pipeline runs, because local runs can't see rates near 1 in 300, and report the suite's combined rate against the 5% budget.

### Suggested targets

| Measure | Target |
|---|---|
| Critical logic mutation score (scheduling, validation, retention) | 80%+ |
| Unit and in-process flow suite | Under about 30s |
| Suite flake rate | Under 5%, combined |
| `[NotInParallel]` classes | 0 |
| Real-system tests | A few minutes, parallel across the pool |
