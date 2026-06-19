using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.FailureHandlers;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class ReconcileCoordinatorTests
{
    private sealed record Fixture(
        PipelineService Pipelines,
        ProductionStepService Production,
        EntityStore<Job> JobStore,
        ReconcilePauseState PauseState,
        IdProvider IdProvider,
        JobService Jobs,
        PipelineReconciler Reconciler);

    private static Fixture CreateFixture()
    {
        var idProvider = new IdProvider();
        var pipelineStore = new EntityStore<Pipeline>([]);
        var productionStore = new EntityStore<ProductionStep>([]);
        var processingStore = new EntityStore<ProcessingStep>([]);
        var triggerStore = new EntityStore<Trigger>([]);
        var jobStore = new EntityStore<Job>([]);

        var pipelines = new PipelineService(pipelineStore, idProvider);
        var production = new ProductionStepService(
            productionStore, new AttachmentStore<ProductionStep, StepConfiguration>(productionStore), idProvider);
        var processing = new ProcessingStepService(
            processingStore, new AttachmentStore<ProcessingStep, StepConfiguration>(processingStore), idProvider);
        var triggers = new TriggerService(triggerStore, idProvider);
        var failureHandlers = new FailureHandlerBindingService(new AttachmentStore<Pipeline, FailureHandlerBindings>(pipelineStore));
        var reconciler = new PipelineReconciler(pipelines, production, processing, triggers, failureHandlers);
        var jobs = new JobService(NullLogger<JobService>.Instance, jobStore,
            new JobGroupService(new EntityStore<JobGroup>([]), idProvider, TimeProvider.System),
            idProvider, TimeProvider.System);

        return new Fixture(pipelines, production, jobStore, new ReconcilePauseState(), idProvider, jobs, reconciler);
    }

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    private static PipelineManifest Manifest(params string[] productionStepNames)
        => new("0.0", "p", null, null, null,
            productionStepNames.Select(n => new ProductionStepDocument(n, new StepConfigurationDocument("alpine", "echo", null))).ToArray(),
            [], []);

    private ReconcileCoordinator Coordinator(Fixture f, ReconcileOptions options)
        => new(f.PauseState, f.Reconciler, f.Jobs, options, TimeProvider.System, NullLogger<ReconcileCoordinator>.Instance);

    [Test]
    public async Task ReconcileAsync_WhenQuiescent_AppliesAndClearsPause()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));
        var coordinator = Coordinator(f, new ReconcileOptions());

        var result = await coordinator.ReconcileAsync(pipeline.Id, Manifest("build"));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Pick(f.Production.GetByPipelineId(pipeline.Id)).Length).IsEqualTo(1);
        await Assert.That(f.PauseState.IsPaused(pipeline.Id)).IsFalse();
    }

    [Test]
    public async Task ReconcileAsync_WhenJobNeverDrains_TimesOutAndClearsPause()
    {
        var f = CreateFixture();
        var pipeline = Pick(f.Pipelines.Create("p"));

        // A scheduled job that never terminates — drain can never complete.
        f.JobStore.Set(new Job.ProductionJob(
            f.IdProvider.Create<Job>(), pipeline.Id, DateTimeOffset.UtcNow,
            new JobStatus.Scheduled(), f.IdProvider.Create<JobGroup>(), f.IdProvider.Create<ProductionStep>()));

        var coordinator = Coordinator(f, new ReconcileOptions { DrainTimeout = TimeSpan.Zero });

        var result = await coordinator.ReconcileAsync(pipeline.Id, Manifest("build"));

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(f.PauseState.IsPaused(pipeline.Id)).IsFalse();
        // Nothing applied — the mutate never ran.
        await Assert.That(Pick(f.Production.GetByPipelineId(pipeline.Id)).Length).IsEqualTo(0);
    }
}
