using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class ReconcileCoordinatorTests
{
    private sealed record Fixture(
        PipelineService Pipelines,
        EntityStore<Attempt> JobStore,
        ReconcilePauseState PauseState,
        AttemptService Attempts);

    private static Fixture CreateFixture()
    {
        var idProvider = new IdProvider();
        var jobStore = new EntityStore<Attempt>([]);

        var pipelines = new PipelineService(new EntityStore<Pipeline>([]), idProvider);
        var attempts = new AttemptService(jobStore, new EntityStore<Run>([]), idProvider, TimeProvider.System);

        return new Fixture(pipelines, jobStore, new ReconcilePauseState(), attempts);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    private static ReconcileCoordinator Coordinator(Fixture f, ReconcileOptions options)
        => new(f.PauseState, f.Attempts, options, TimeProvider.System, NullLogger<ReconcileCoordinator>.Instance);

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

        // A scheduled attempt that never terminates — drain can never complete.
        f.JobStore.Set(TestAttempts.Make(pipeline.Id));

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
