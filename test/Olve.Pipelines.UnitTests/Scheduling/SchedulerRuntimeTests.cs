using Olve.Results.TUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>Restart resume, the startup sweep, the executor launch spec, and a run end to end through
/// the real <see cref="JobRunner"/> + <see cref="NoOpJobExecutor"/>.</summary>
public class SchedulerRuntimeTests
{
    /// <summary>A fresh domain holding a copy of <paramref name="source"/>'s state, loaded before any event
    /// wiring (as persistence does), so nothing reacts to the load itself.</summary>
    private static TestRunDomain Restore(TestRunDomain source, Action<TestRunDomain>? beforeWiring = null, bool keepPipelines = true)
    {
        var target = new TestRunDomain(source.Time);
        if (keepPipelines)
            foreach (var p in source.Pipelines.List()) target.Pipelines.Set(p);
        foreach (var v in source.Versions.List()) target.Versions.Set(v);
        foreach (var (id, a) in source.Activations.GetAll()) target.Activations.Set(id, a);
        foreach (var (id, seq) in source.Sequences.GetAll()) target.Sequences.Set(id, seq);
        foreach (var (id, g) in source.BlockedSteps.GetAll()) target.BlockedSteps.Set(id, g);
        foreach (var (id, p) in source.PendingStarts.GetAll()) target.PendingStarts.Set(id, p);
        foreach (var r in source.Runs.List()) target.Runs.Set(r);
        foreach (var a in source.Attempts.List()) target.Attempts.Set(a);
        beforeWiring?.Invoke(target);
        return target.WireEvents(scheduler: true);
    }

    private static RunSweep Sweep(TestRunDomain d) => new(d.Pipelines, d.Runs, d.Scheduler, d.Switches, d.SchedulingLock);

    private static void RunStartup(TestRunDomain d)
    {
        var startup = new RunSchedulingStartup(d.Pipelines, d.Runs, d.RunService, d.AttemptService, Sweep(d),
            d.SchedulingLock, NullLogger<RunSchedulingStartup>.Instance);
        if (startup.Run().TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.ToString())));
    }

    [Test]
    public async Task Restart_ResumesRunWhoseStageFinishedWhileDown_Idempotently()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        var checkout = s.MarkInProgress(run, "checkout");

        // The attempt finished while the service was down: the restored state has it Succeeded, but no
        // event ever advanced the run.
        var restored = Restore(s.D, d =>
        {
            if (d.AttemptService.MarkSucceeded(checkout.Id, DefaultOutput(run.Number, "checkout", 1)).TryPickProblems(out _))
                throw new InvalidOperationException("could not finish checkout");
        });

        RunStartup(restored);
        RunStartup(restored);

        var steps = restored.AttemptService.ListByRun(run.Id).GroupBy(a => a.StepName).ToDictionary(g => g.Key, g => g.Count());
        await Assert.That(steps).IsEquivalentTo(new Dictionary<string, int> { ["checkout"] = 1, ["build"] = 1, ["test"] = 1 });
        await Assert.That(restored.AttemptService.ListForStep(run.Id, "build")[0].Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task Sweep_AdvancesARunWhoseAdvanceWasMissed()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        var checkout = s.MarkInProgress(run, "checkout");

        // As if the completion's event handler threw: checkout is Succeeded but nothing advanced the run.
        var missed = Restore(s.D, d =>
        {
            if (d.AttemptService.MarkSucceeded(checkout.Id, DefaultOutput(run.Number, "checkout", 1)).TryPickProblems(out _))
                throw new InvalidOperationException("could not finish checkout");
        });
        await Assert.That(missed.AttemptService.ListForStep(run.Id, "build")).IsEmpty();

        Sweep(missed).Sweep();

        await Assert.That(missed.AttemptService.ListForStep(run.Id, "build")[0].Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task Restart_LeavesInFlightAttemptsAlone()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.MarkInProgress(run, "checkout");

        var restored = Restore(s.D);
        RunStartup(restored);

        var attempts = restored.AttemptService.ListByRun(run.Id);
        await Assert.That(attempts).Count().IsEqualTo(1);
        await Assert.That(attempts[0].Status).IsTypeOf<InProgress>();
    }

    [Test]
    public async Task Startup_DropsRunsOfDeletedPipelines()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());

        var restored = Restore(s.D, keepPipelines: false);
        RunStartup(restored);

        await Assert.That(restored.Runs.Contains(run.Id)).IsFalse();
        await Assert.That(restored.Attempts.List()).IsEmpty();
    }

    [Test]
    public async Task Startup_CompletesALeftoverDrain_AndStartsQueuedRun()
    {
        var s = new SchedulerScenario();
        var old = s.Start(Input());
        var checkout = s.MarkInProgress(old, "checkout");
        var v2 = s.AddVersion();
        // A drain persisted by an earlier release (switches complete at once now), with a queued request.
        TestRunDomain.Pick(s.D.ActivationService.BeginSwitch(s.Pipeline.Id, v2.Id));
        await Assert.That(s.StartOutcome(Input(sha: OtherSha))).IsTypeOf<RunStartOutcome.Queued>();

        var restored = Restore(s.D, d =>
        {
            if (d.AttemptService.MarkFailed(checkout.Id, "pod lost").TryPickProblems(out _))
                throw new InvalidOperationException();
        });
        RunStartup(restored);

        await Assert.That(restored.ActivationService.TryGet(s.Pipeline.Id, out var activation)).IsTrue();
        await Assert.That(activation).IsTypeOf<PipelineActivation.Active>();
        var newest = restored.RunService.ListByPipeline(s.Pipeline.Id)[0];
        await Assert.That(newest.VersionId).IsEqualTo(v2.Id);
        await Assert.That(restored.AttemptService.ListForStep(newest.Id, "checkout")).Count().IsEqualTo(1);
        restored.Runs.TryGet(old.Id, out var oldAfter);
        await Assert.That(oldAfter!.Status).IsEqualTo(RunStatus.Failed);
    }

    [Test]
    public async Task LaunchSpec_CarriesInputFileRefsAndEnvironment()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");
        var deploy = s.Latest(run, "deploy-beta")!;

        var spec = TestRunDomain.Pick(s.D.Launches.Get(deploy.Id));

        await Assert.That(spec.InputJson).IsEqualTo(deploy.Input!);
        await Assert.That(spec.InputHash).IsEqualTo(deploy.InputHash);
        await Assert.That(spec.Step.Name).IsEqualTo("deploy-beta");
        await Assert.That(spec.Step.Image).IsEqualTo("alpine:latest");
        await Assert.That(spec.BinaryKey).IsEqualTo(s.Version.BinaryKey);
        await Assert.That(spec.FileRefs.Select(f => f.FileKey).ToList())
            .IsEquivalentTo([$"{run.Number}/build/1/chart/", $"{run.Number}/build/1/image.tar"]);
        var chart = spec.FileRefs.Single(f => f.Path == "chart/");
        await Assert.That(chart.ObjectPath).IsEqualTo("chart.tar");
        await Assert.That(chart.JsonPath).IsEqualTo("$.artifact.chart");
        await Assert.That(chart.StorageKey(s.Pipeline.Id))
            .IsEqualTo($"runs/{s.Pipeline.Id.Value.Value:N}/{run.Number}/build/1/files/chart.tar");
        await Assert.That(spec.Environment["OLVE_RUN"]).IsEqualTo(run.Number.ToString());
        await Assert.That(spec.Environment["OLVE_ATTEMPT"]).IsEqualTo("1");
        await Assert.That(spec.Environment["OLVE_VERSION_STAMP"]).IsEqualTo(run.VersionStamp);
        await Assert.That(spec.Environment["OLVE_RUN_ID"]).IsEqualTo(run.Id.Value.Value.ToString());
        await Assert.That(spec.Environment["OLVE_PIPELINE"]).IsEqualTo(s.Pipeline.Id.Value.Value.ToString());

        // A scheduler-skipped attempt has nothing to launch.
        var skipped = s.D.AttemptService.RecordSkipped(run.Id, "summary", "input 'beta' needs deploy-beta (skipped: test)").Value!;
        await Assert.That(s.D.Launches.Get(skipped.Id)).Failed();
    }

    private sealed class NullApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Test]
    public async Task EndToEnd_ThroughJobRunnerAndNoOpExecutor()
    {
        var s = new SchedulerScenario();
        var d = s.D;
        var pending = new NoOpJobExecutorPendingStore();
        var registry = new JobWatcherRegistry(NullLogger<JobWatcherRegistry>.Instance);
        var lifetime = new NullApplicationLifetime();
        var services = new ServiceCollection();
        services.AddSingleton(d.Attempts);
        services.AddSingleton(d.AttemptService);
        services.AddTransient(_ => new JobQueueService(d.Attempts));
        services.AddTransient<IJobExecutor>(sp => new NoOpJobExecutor(
            sp.GetRequiredService<IServiceScopeFactory>(), registry, lifetime, pending, d.AttemptService,
            NullLogger<NoOpJobExecutor>.Instance));
        await using var sp = services.BuildServiceProvider();
        var runner = new JobRunner(sp, registry, NullLogger<JobRunner>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = runner.StartAsync(cts.Token);

        var run = s.Start(Input(deployTo: "beta"));
        foreach (var stage in new[] { new[] { "checkout" }, ["build", "test"], ["deploy-beta"], ["summary"] })
        {
            foreach (var step in stage)
            {
                var attempt = await WaitFor(() => s.Latest(run, step) is { } a && pending.HasPendingJob(a.Id) ? a : null);
                pending.Finish(attempt.Id, new NoOpJobResult.Success(DefaultOutput(run.Number, step, attempt.Number)));
            }
        }

        await WaitFor(() => s.Get(run).Status is RunStatus.Succeeded ? s.Get(run) : null);
        await Assert.That(s.Latest(run, "deploy-prod")).IsNull();
        await cts.CancelAsync();
        await runner.StopAsync(CancellationToken.None);
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if (probe() is { } value)
                return value;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
