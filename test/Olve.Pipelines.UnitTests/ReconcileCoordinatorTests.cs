using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class ReconcileCoordinatorTests
{
    private sealed record Fixture(
        PipelineService Pipelines,
        EntityStore<Job> JobStore,
        ReconcilePauseState PauseState,
        JobService Jobs);

    private static Fixture CreateFixture()
    {
        var idProvider = new IdProvider();
        var jobStore = new EntityStore<Job>([]);

        var pipelines = new PipelineService(new EntityStore<Pipeline>([]), idProvider);
        var jobs = new JobService(NullLogger<JobService>.Instance, jobStore, idProvider, TimeProvider.System);

        return new Fixture(pipelines, jobStore, new ReconcilePauseState(), jobs);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    private static ReconcileCoordinator Coordinator(Fixture f, ReconcileOptions options)
        => new(f.PauseState, f.Jobs, options, TimeProvider.System, NullLogger<ReconcileCoordinator>.Instance);

    [Test]
    public async Task ReconcileAsync_WhenQuiescent_AppliesAndClearsPause()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));
        var coordinator = Coordinator(f, new ReconcileOptions());
        var pausedDuringMutate = false;

        var result = await coordinator.ReconcileAsync(pipeline.Id, () =>
        {
            pausedDuringMutate = f.PauseState.IsPaused(pipeline.Id);
            return Result.Success();
        });

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(pausedDuringMutate).IsTrue();
        await Assert.That(f.PauseState.IsPaused(pipeline.Id)).IsFalse();
    }

    [Test]
    public async Task ReconcileAsync_WhenJobNeverDrains_TimesOutAndClearsPause()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        // A scheduled job that never terminates — drain can never complete.
        f.JobStore.Set(TestJobs.Make(pipeline.Id));

        var coordinator = Coordinator(f, new ReconcileOptions { DrainTimeout = TimeSpan.Zero });
        var mutated = false;

        var result = await coordinator.ReconcileAsync(pipeline.Id, () =>
        {
            mutated = true;
            return Result.Success();
        });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(f.PauseState.IsPaused(pipeline.Id)).IsFalse();
        // Nothing applied — the mutate never ran.
        await Assert.That(mutated).IsFalse();
    }
}
