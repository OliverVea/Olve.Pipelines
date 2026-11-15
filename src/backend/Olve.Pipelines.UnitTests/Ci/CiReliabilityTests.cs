using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using TUnit.Assertions.Enums;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>
/// The CI reliability report judges a step's failure by the same step on the same tree with the same code: passed there
/// (flaky), failed there again (reproduced), or fixed by a later head of the same pull request (caught).
/// </summary>
public class CiReliabilityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T10:00:00Z");

    /// <summary>
    /// An attempt's outcome: <c>s</c> succeeded, <c>f</c> its own failure, <c>t</c> timed out, <c>i</c> an infrastructure
    /// failure, <c>r</c> reused (a success that ran no Job), <c>n</c> never run.
    /// </summary>
    private static CiAttempt Attempt(int run, string step, char outcome, int number = 1)
    {
        var at = T0.AddMinutes(run * 10 + number);
        AttemptStatus status = outcome switch
        {
            's' or 'r' => new Succeeded(at, at),
            'f' => new Failed(at, at, "boom", AttemptFailureKind.Handler),
            't' => new Failed(at, at, "step timed out", AttemptFailureKind.DeadlineExceeded),
            'i' => new Failed(at, at, "pod lost", AttemptFailureKind.PodFailed),
            'n' => new Failed(at, at, "Input of step", AttemptFailureKind.NotRun),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        return new CiAttempt(Id.New<CiAttempt>(), Id.New<Pipeline>(), Id.New<CiRun>(), run, step, number, status, at,
            ReusedFrom: outcome == 'r' ? new CiReuse(Id.New<CiAttempt>(), 1, step, 1) : null);
    }

    /// <summary>A pull run of <paramref name="pull"/> at <paramref name="head"/>; each outcome is one attempt of <c>test</c> (attempts numbered in order).</summary>
    private static CiReliabilityRun Run(int number, string tree, string? code, int pull, string head, string outcomes, string step = "test") =>
        new(number, tree, code, [(pull, head)], false, [.. outcomes.Select((o, i) => Attempt(number, step, o, i + 1))]);

    private static CiStepReliability Step(CiReliabilityReport report, string step = "test") => report.Steps.Single(s => s.Step == step);

    public static IEnumerable<Func<(string Case, CiReliabilityRun[] History, (int Flaky, int Reproduced, int Caught, int Unverified) Expected)>> Classifications()
    {
        yield return () => ("passed on the same tree and code: flaky",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t1", "c", 7, "A", "s")], (1, 0, 0, 0));
        yield return () => ("passed earlier on the same tree and code: flaky",
            [Run(1, "t1", "c", 7, "A", "s"), Run(2, "t1", "c", 8, "B", "f")], (1, 0, 0, 0));
        yield return () => ("failed twice, then passed: both flaky, not reproduced",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t1", "c", 7, "A", "f"), Run(3, "t1", "c", 7, "A", "s")], (2, 0, 0, 0));
        yield return () => ("failed again on the same tree and code, never passed: reproduced",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t1", "c", 7, "A", "f")], (0, 2, 0, 0));
        yield return () => ("a timeout again counts as failing again",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t1", "c", 7, "A", "t")], (0, 2, 0, 0));
        yield return () => ("passed on the same tree with other code: not flaky",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t1", "d", 7, "A", "s")], (0, 0, 0, 1));
        yield return () => ("a later head of the same pull request passed: caught",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t2", "c", 7, "B", "s")], (0, 0, 1, 0));
        yield return () => ("caught needs another head: the same head on a moved base is not",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t2", "c", 7, "A", "s")], (0, 0, 0, 1));
        yield return () => ("caught needs the same pull request",
            [Run(1, "t1", "c", 7, "A", "f"), Run(2, "t2", "c", 8, "B", "s")], (0, 0, 0, 1));
        yield return () => ("caught needs a later run",
            [Run(1, "t2", "c", 7, "B", "s"), Run(2, "t1", "c", 7, "A", "f")], (0, 0, 0, 1));
        yield return () => ("a timeout is never caught",
            [Run(1, "t1", "c", 7, "A", "t"), Run(2, "t2", "c", 7, "B", "s")], (0, 0, 0, 1));
        yield return () => ("an infrastructure failure is none of them",
            [Run(1, "t1", "c", 7, "A", "i"), Run(2, "t1", "c", 7, "A", "s")], (0, 0, 0, 0));
        yield return () => ("without a code hash only caught is known",
            [Run(1, "t1", null, 7, "A", "f"), Run(2, "t1", null, 7, "A", "s")], (0, 0, 0, 1));
        yield return () => ("never ran again and no later head: unverified",
            [Run(1, "t1", "c", 7, "A", "f")], (0, 0, 0, 1));
    }

    [Test]
    [MethodDataSource(nameof(Classifications))]
    public async Task A_failure_is_classified(string @case, CiReliabilityRun[] history, (int Flaky, int Reproduced, int Caught, int Unverified) expected)
    {
        var step = Step(CiReliability.Report(history, 200));

        await Assert.That((step.FlakyFailures, step.ReproducedFailures, step.CaughtFailures, step.UnverifiedFailures)).IsEqualTo(expected);
    }

    [Test]
    public async Task Counts_split_failures_by_kind_and_leave_out_reused_and_never_run_attempts()
    {
        var step = Step(CiReliability.Report([Run(1, "t1", "c", 7, "A", "sftinr")], 200));

        await Assert.That((step.Attempts, step.Failures, step.OwnFailures, step.InfrastructureFailures, step.Timeouts))
            .IsEqualTo((4, 3, 1, 1, 1));
    }

    [Test]
    public async Task A_flaky_failure_names_the_attempt_that_passed_and_the_pull_requests()
    {
        var step = Step(CiReliability.Report([Run(1, "t1", "c", 7, "A", "f"), Run(2, "t1", "c", 7, "A", "s")], 200));

        await Assert.That(step.Flaky).IsEquivalentTo([
            new CiFlakyFailure(new CiAttemptRef(1, "test", 1), new CiAttemptRef(2, "test", 1), [7], T0.AddMinutes(11)),
        ]);
        await Assert.That(step.FlakyRate).IsEqualTo(0.5);
    }

    [Test]
    public async Task Matrix_cells_are_judged_separately()
    {
        var report = CiReliability.Report([
            new CiReliabilityRun(1, "t1", "c", [(7, "A")], false, [Attempt(1, "build[os=linux]", 'f'), Attempt(1, "build[os=windows]", 'f')]),
            new CiReliabilityRun(2, "t1", "c", [(7, "A")], false, [Attempt(2, "build[os=linux]", 's'), Attempt(2, "build[os=windows]", 'f')]),
        ], 200);

        await Assert.That(report.Steps.Select(s => (s.Step, s.FlakyFailures, s.ReproducedFailures))).IsEquivalentTo([
            ("build[os=linux]", 1, 0), ("build[os=windows]", 0, 2),
        ]);
    }

    [Test]
    public async Task The_flakiest_steps_come_first()
    {
        var report = CiReliability.Report([
            new CiReliabilityRun(1, "t1", "c", [(7, "A")], false, [Attempt(1, "a", 'f'), Attempt(1, "b", 'f'), Attempt(1, "c", 's')]),
            new CiReliabilityRun(2, "t1", "c", [(7, "A")], false, [Attempt(2, "a", 's'), Attempt(2, "b", 's'), Attempt(2, "b", 's', 2), Attempt(2, "c", 's')]),
        ], 200);

        await Assert.That(report.Steps.Select(s => s.Step)).IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_window_covers_the_latest_runs_and_judges_them_against_the_whole_history()
    {
        var report = CiReliability.Report([Run(1, "t1", "c", 7, "A", "s"), Run(2, "t1", "c", 8, "B", "f"), Run(3, "t9", "c", 9, "C", "s")], 2);

        await Assert.That((report.FromRun, report.ToRun, report.Runs, report.RequestedRuns)).IsEqualTo((2, 3, 2, 2));
        await Assert.That((Step(report).Attempts, Step(report).FlakyFailures)).IsEqualTo((2, 1));
    }

    [Test]
    public async Task A_run_without_a_code_hash_is_noted()
    {
        var report = CiReliability.Report([Run(1, "t1", null, 7, "A", "f")], 200);

        await Assert.That(report.Notes).IsEquivalentTo([
            "1 of the runs have no code hash (their definition can't be read), so their failures count only as caught or unverified.",
        ]);
    }

    public static IEnumerable<Func<int>> OutOfRange()
    {
        yield return () => 0;
        yield return () => 1001;
    }

    [Test]
    [MethodDataSource(nameof(OutOfRange))]
    public async Task Runs_out_of_range_are_refused(int runs)
    {
        await using var s = new CiScenario();

        var result = s.Get<CiReliabilityService>().Get(s.Pipeline.Id, runs);

        await result.Problems.IsSingle<ValidationProblem>($"runs: runs must be between 1 and 1000, not {runs}.");
    }

    [Test]
    public async Task A_step_that_failed_and_passed_when_the_pull_request_ran_again_is_flaky()
    {
        await using var s = new CiScenario(r => r["codeHash"] = "sha256:" + new string('c', 64));
        s.Push(7, "a111111111111111111111111111111111111111");
        await s.SettleAsync();
        var first = s.Run(7);
        s.Succeed(first, "checkout");
        s.Fail(first, "build");
        s.Succeed(first, "test");
        TestRunDomain.Pick(await s.Get<CiRunStartService>().StartAsync(s.Pipeline.Id, 7));
        s.Succeed(s.Run(7), "build");

        var response = await new GetCiReliabilityHandler(s.Get<PipelineService>(), s.Get<CiReliabilityService>())
            .HandleAsync(new Contract.CiRunsReliabilityRequest(s.Pipeline.Id.Value.Value, null), default);

        var build = ((Contract.CiRunsReliabilityResponse.Ok)response).Body.Steps.Single(x => x.Step == "build");
        await Assert.That((build.Attempts, build.FlakyFailures, build.Flaky.Single().Passed.Run)).IsEqualTo((2, 1, 2));
    }
}
