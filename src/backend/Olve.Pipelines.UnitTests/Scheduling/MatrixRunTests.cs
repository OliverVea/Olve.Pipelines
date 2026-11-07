using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>A matrix step (<c>build</c>: os × arch) runs one attempt per cell; deploy-beta reads it through a cells input.</summary>
public class MatrixRunTests
{
    private const string LinuxX64 = "build[os=linux,arch=x64]";
    private const string LinuxArm = "build[os=linux,arch=arm64]";
    private const string WindowsX64 = "build[os=windows,arch=x64]";
    private const string WindowsArm = "build[os=windows,arch=arm64]";
    private static readonly string[] Cells = [LinuxX64, LinuxArm, WindowsX64, WindowsArm];

    private static SchedulerScenario Scenario(string failure = "keepRunning", long? maxParallel = null, long? retries = null) =>
        new(Mutated(r =>
        {
            ManifestFixture.MatrixBuild(r, ManifestFixture.Matrix(failure, maxParallel));
            if (retries is { } n) ManifestFixture.Step(r, "build")["retries"] = n;
        }));

    private static string Output(string cell) => $$"""{"version":"{{cell}}"}""";

    private static Run StartAtBuild(SchedulerScenario s)
    {
        var run = s.Start(Input(deployTo: "beta"));
        s.Succeed(run, "checkout");
        s.Succeed(run, "test");
        return run;
    }

    private static RunStepView BuildView(SchedulerScenario s, Run run)
        => TestRunDomain.Pick(s.D.Views.Get(s.Pipeline.Id, run.Number)).Stages[1].Steps.Single(st => st.Name == "build");

    [Test]
    public async Task EveryCellRuns_TheBarrierWaitsForAll_AndTheReaderGetsTheListInCellOrder()
    {
        var s = Scenario();
        var run = StartAtBuild(s);

        await Assert.That(s.Active(run)).IsEquivalentTo(Cells);
        foreach (var cell in new[] { WindowsArm, WindowsX64, LinuxArm })
            s.Succeed(run, cell, Output(cell));
        await Assert.That(s.Latest(run, "deploy-beta")).IsNull();
        await Assert.That(BuildView(s, run).Status).IsEqualTo(Contract.RunStepStatus.Scheduled);

        s.Succeed(run, LinuxX64, Output(LinuxX64));

        var input = s.Latest(run, "deploy-beta")!.Input!;
        await Assert.That(input).IsEqualTo(
            """{"artifact":[{"cell":{"os":"linux","arch":"x64"},"output":{"version":"build[os=linux,arch=x64]"}},"""
            + """{"cell":{"os":"linux","arch":"arm64"},"output":{"version":"build[os=linux,arch=arm64]"}},"""
            + """{"cell":{"os":"windows","arch":"x64"},"output":{"version":"build[os=windows,arch=x64]"}},"""
            + """{"cell":{"os":"windows","arch":"arm64"},"output":{"version":"build[os=windows,arch=arm64]"}}]}""");
        var view = BuildView(s, run);
        await Assert.That(view.Status).IsEqualTo(Contract.RunStepStatus.Succeeded);
        await Assert.That(view.Cells!.Select(c => c.Name)).IsEquivalentTo(Cells, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(view.AttemptCount).IsEqualTo(4);
    }

    [Test]
    public async Task ACellsJob_RunsItsMatrixStep_WithItsCell()
    {
        var s = Scenario();
        var run = StartAtBuild(s);

        var spec = TestRunDomain.Pick(s.D.Launches.Get(s.Latest(run, WindowsArm)!.Id));
        var request = HarnessJobRequest.ForAttempt(spec.Attempt, run.VersionStamp, spec.Step.Image, spec.Step.TimeoutSeconds, spec.Step.Secrets,
            new HarnessUrls("https://step", null, null, new Dictionary<string, string>()), cell: spec.Cell);
        var job = TestRunDomain.Pick(HarnessJobBuilder.Build(request, new HarnessOptions())).Job;
        var container = job.Spec.Template.Spec.Containers[0];

        await Assert.That(spec.Step.Name).IsEqualTo("build");
        await Assert.That(container.Command![^2..]).IsEquivalentTo(["run", "build"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(container.Env!.Single(e => e.Name == "OLVE_CELL").Value).IsEqualTo("""{"os":"windows","arch":"arm64"}""");
        await Assert.That(job.Metadata.Labels!["olve-step"]).IsEqualTo("build");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SkippedCells_AreLeftOutOfTheList_AndAllSkippedSkipsTheReader(bool allSkipped)
    {
        var s = Scenario();
        var run = StartAtBuild(s);

        foreach (var cell in Cells)
        {
            var attempt = s.MarkInProgress(run, cell);
            if (allSkipped || cell == LinuxArm)
                await Assert.That(s.D.AttemptService.MarkSkipped(attempt.Id, $"nothing for {cell}")).Succeeded();
            else
                await Assert.That(s.D.AttemptService.MarkSucceeded(attempt.Id, Output(cell))).Succeeded();
        }

        var reader = s.Latest(run, "deploy-beta")!;
        var view = BuildView(s, run);
        if (allSkipped)
        {
            await Assert.That(IsSchedulerSkip(reader, out var reason)).IsTrue();
            await Assert.That(reason).IsEqualTo($"input 'artifact' needs build (skipped: nothing for {LinuxX64})");
            await Assert.That(view.Status).IsEqualTo(Contract.RunStepStatus.Skipped);
            await Assert.That(view.Reason).IsEqualTo($"nothing for {LinuxX64}");
        }
        else
        {
            var cells = JsonNode.Parse(reader.Input!)!["artifact"]!.AsArray().Select(c => c!["output"]!["version"]!.GetValue<string>());
            await Assert.That(cells).IsEquivalentTo([LinuxX64, WindowsX64, WindowsArm], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(view.Status).IsEqualTo(Contract.RunStepStatus.Succeeded);
            await Assert.That(view.Reason).IsNull();
        }
    }

    [Test]
    [Arguments("keepRunning")]
    [Arguments("cascadeFailure")]
    public async Task AFailedCell_FailsTheStep_AndCascadesOnlyWhenTheStepSaysSo(string failure)
    {
        var s = Scenario(failure);
        var run = StartAtBuild(s);
        s.MarkInProgress(run, WindowsX64);

        s.Fail(run, LinuxArm, "link error");

        var others = new[] { LinuxX64, WindowsX64, WindowsArm };
        if (failure == "keepRunning")
        {
            await Assert.That(s.Active(run)).IsEquivalentTo(others);
            await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);
            foreach (var cell in others) s.Succeed(run, cell, Output(cell));
        }
        else
        {
            await Assert.That(s.Active(run)).IsEmpty();
            foreach (var cell in others)
                await Assert.That(((Cancelled)s.Latest(run, cell)!.Status).Reason).IsEqualTo("cancelled: linux · arm64 failed");
        }

        var view = BuildView(s, run);
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
        await Assert.That(s.Latest(run, "deploy-beta")).IsNull();
        await Assert.That(view.Status).IsEqualTo(Contract.RunStepStatus.Failed);
        await Assert.That(view.Reason).IsEqualTo("linux · arm64 failed: link error");
        if (failure == "cascadeFailure")
            await Assert.That(view.Cells!.Single(c => c.Name == WindowsX64).Reason).IsEqualTo("cancelled: linux · arm64 failed");
    }

    [Test]
    public async Task AFailureThatWillBeRetried_DoesNotCascade()
    {
        var s = Scenario("cascadeFailure", retries: 1);
        var run = StartAtBuild(s);

        s.Fail(run, LinuxX64);

        await Assert.That(s.All(run, LinuxX64).Select(a => a.Status.Discriminator())).IsEquivalentTo(["failed", "scheduled"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(s.Active(run)).IsEquivalentTo(Cells);

        s.Fail(run, LinuxX64, "still broken");

        await Assert.That(s.Active(run)).IsEmpty();
        await Assert.That(((Cancelled)s.Latest(run, WindowsArm)!.Status).Reason).IsEqualTo("cancelled: linux · x64 failed");
        await Assert.That(BuildView(s, run).Reason).IsEqualTo("linux · x64 failed: still broken");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ACellCancelledByACascade_CanBeRerun(bool continueAfter)
    {
        var s = Scenario("cascadeFailure");
        var run = StartAtBuild(s);
        s.Fail(run, LinuxX64);
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);

        var rerun = s.D.Continuations.RerunStep(run.Id, WindowsArm, continueAfter);

        await Assert.That(rerun).Succeeded();
        await Assert.That(s.Latest(run, WindowsArm)!.Status).IsTypeOf<Scheduled>();
        await Assert.That(s.Get(run).Status).IsEqualTo(continueAfter ? RunStatus.Running : RunStatus.Failed);
        s.Succeed(run, WindowsArm, Output(WindowsArm));
        await Assert.That(s.Latest(run, WindowsArm)!.Status).IsTypeOf<Succeeded>();
    }

    [Test]
    public async Task TheMatrixStepsName_RerunsEveryCell()
    {
        var s = Scenario();
        var run = StartAtBuild(s);
        foreach (var cell in Cells) s.Succeed(run, cell, Output(cell));
        s.Succeed(run, "deploy-beta");
        s.Succeed(run, "summary");

        var rerun = s.D.Continuations.RerunStep(run.Id, "build", continueAfter: false);

        await Assert.That(rerun).Succeeded();
        await Assert.That(TestRunDomain.Pick(rerun).StepName).IsEqualTo(LinuxX64);
        foreach (var cell in Cells)
            await Assert.That(s.All(run, cell).Count).IsEqualTo(2);
        await Assert.That(s.Active(run)).IsEquivalentTo(Cells);
    }

    [Test]
    public async Task BlockingTheMatrixStep_HoldsEveryCell_AndACellCannotBeBlocked()
    {
        var s = Scenario();
        await Assert.That(s.D.GateService.Block(s.Pipeline.Id, "build")).Succeeded();
        var run = StartAtBuild(s);

        await Assert.That(s.Active(run)).IsEmpty();
        await Assert.That(s.Get(run).HeldAt).IsEqualTo("build");
        var view = BuildView(s, run);
        await Assert.That(view.Status).IsEqualTo(Contract.RunStepStatus.Held);
        await Assert.That(view.Cells!.All(c => c.Status == Contract.RunStepStatus.Held)).IsTrue();
        await s.D.GateService.Block(s.Pipeline.Id, LinuxX64).Problems.IsSingle<ResultProblem>(
            "'build[os=linux,arch=x64]' is a cell of matrix step 'build'; block 'build', which holds every cell.");

        await Assert.That(s.D.GateService.Unblock(s.Pipeline.Id, "build")).Succeeded();

        await Assert.That(s.Active(run)).IsEquivalentTo(Cells);
    }

    [Test]
    public async Task MaxParallel_HoldsTheRestOfTheCells_AtDispatch()
    {
        var s = Scenario(maxParallel: 2);
        var d = s.D;
        var pending = new NoOpJobExecutorPendingStore();
        var registry = new JobWatcherRegistry(NullLogger<JobWatcherRegistry>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(d.Attempts);
        services.AddSingleton(d.AttemptService);
        services.AddTransient(_ => new JobQueueService(d.Attempts));
        services.AddTransient(_ => new MatrixParallelism(d.RunService, d.Plans));
        services.AddTransient<IJobExecutor>(sp => new NoOpJobExecutor(
            sp.GetRequiredService<IServiceScopeFactory>(), registry, new NullLifetime(), pending, d.AttemptService,
            NullLogger<NoOpJobExecutor>.Instance));
        await using var sp = services.BuildServiceProvider();
        var runner = new JobRunner(sp, registry, new JobDispatchSignal(), NullLogger<JobRunner>.Instance) { MaxConcurrentJobs = 10 };
        var run = s.Start(Input(deployTo: "beta"));
        s.Succeed(run, "checkout");

        runner.DispatchTick();

        await Assert.That(Cells.Select(c => registry.IsRunning(s.Latest(run, c)!.Id))).IsEquivalentTo([true, true, false, false], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(registry.IsRunning(s.Latest(run, "test")!.Id)).IsTrue();

        var first = s.Latest(run, LinuxX64)!;
        await pending.WhenPending(first.Id).WaitAsync(TimeSpan.FromSeconds(10));
        pending.Finish(first.Id, new NoOpJobResult.Success(Output(LinuxX64)));
        await registry.WhenStopped(first.Id).WaitAsync(TimeSpan.FromSeconds(10));
        runner.DispatchTick();

        await Assert.That(registry.IsRunning(s.Latest(run, WindowsX64)!.Id)).IsTrue();
        await Assert.That(registry.IsRunning(s.Latest(run, WindowsArm)!.Id)).IsFalse();
    }

    private sealed class NullLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

/// <summary>The rules shared with the UI's mock (<c>src/frontend/src/model/matrix.ts</c>).</summary>
public class MatrixRollUpTests
{
    private const Contract.RunStepStatus F = Contract.RunStepStatus.Failed, I = Contract.RunStepStatus.InProgress,
        S = Contract.RunStepStatus.Succeeded, K = Contract.RunStepStatus.Skipped, C = Contract.RunStepStatus.Cancelled,
        Sc = Contract.RunStepStatus.Scheduled, P = Contract.RunStepStatus.Pending, E = Contract.RunStepStatus.Excluded;

    public static IEnumerable<Func<(Contract.RunStepStatus[] Cells, Contract.RunStepStatus Step)>> Rows() =>
        RowValues().Select(r => (Func<(Contract.RunStepStatus[], Contract.RunStepStatus)>)(() => r));

    private static IEnumerable<(Contract.RunStepStatus[] Cells, Contract.RunStepStatus Step)> RowValues() =>
    [
        ([S, S], S),
        ([S, K], S),
        ([K, K], K),
        ([S, F, I], F),
        ([C, F], F),
        ([S, I, Sc], I),
        ([S, Sc, P], Sc),
        ([S, C], C),
        ([E, E], E),
        ([], Contract.RunStepStatus.NotRun),
    ];

    [Test]
    [MethodDataSource(nameof(Rows))]
    public async Task Status(Contract.RunStepStatus[] cells, Contract.RunStepStatus step)
        => await Assert.That(MatrixRollUp.Status(cells)).IsEqualTo(step);

    [Test]
    public async Task Reason_NamesTheFirstFailedCell_AndCountsSeveral()
    {
        var matrix = new ManifestMatrix([new MatrixAxis("os", ["linux", "windows", "macos"])], null, ManifestMatrix.CascadeFailure, null);
        var cells = MatrixCells.Of("build", matrix);

        await Assert.That(MatrixRollUp.Reason([(cells[0], S, null), (cells[1], F, "exit 1"), (cells[2], C, "cancelled: windows failed")]))
            .IsEqualTo("windows failed: exit 1");
        await Assert.That(MatrixRollUp.Reason([(cells[0], F, "exit 2"), (cells[1], F, "exit 1"), (cells[2], S, null)]))
            .IsEqualTo("2 cells failed. linux failed: exit 2");
    }
}
