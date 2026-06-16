using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Pipelines.Polling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Configuration;

public static class ServiceConfiguration
{
    public static void AddPipelineServices(this IServiceCollection services)
    {
        services.AddSingleton<EntityStore<Pipeline>>();
        services.AddSingleton<PipelineEvents>();
        services.AddSingleton<IRunOnStartup, PipelineEventRegistration>();
        services.AddSingleton<EntityStore<ProductionStep>>();
        services.AddSingleton<AttachmentStore<ProductionStep, StepConfiguration>>();
        services.AddSingleton<ProductionStepEvents>();
        services.AddSingleton<IRunOnStartup, ProductionStepEventRegistration>();
        services.AddTransient<ProductionStepService>();
        services.AddTransient<ProductionStepCleanupService>();
        services.AddSingleton<EntityStore<ProcessingStep>>();
        services.AddSingleton<AttachmentStore<ProcessingStep, StepConfiguration>>();
        services.AddSingleton<AttachmentStore<ProcessingStep, ProcessingStepPromotion>>();
        services.AddSingleton<ProcessingStepEvents>();
        services.AddSingleton<IRunOnStartup, ProcessingStepEventRegistration>();
        services.AddTransient<ProcessingStepService>();
        services.AddTransient<ProcessingStepCleanupService>();
        services.AddTransient<PromotionGateService>();
        services.AddSingleton<EntityStore<Trigger>>();
        services.AddSingleton<TriggerEvents>();
        services.AddSingleton<IRunOnStartup, TriggerEventRegistration>();
        services.AddTransient<TriggerService>();
        services.AddTransient<TriggerCleanupService>();
        services.AddTransient<TriggerExecutionService>();
        services.AddSingleton<EntityStore<PipelineConfigBinding>>();
        services.AddTransient<PipelineConfigBindingService>();
        services.AddTransient<PipelineConfigBindingCleanupService>();
        services.AddSingleton<IRunOnStartup, PipelineConfigBindingEventRegistration>();
        services.AddSingleton<IConfigSource, GitHubConfigSource>();
        services.AddHostedService<DeployPollService>();
        services.AddHostedService<PollTriggerService>();
        services.AddTransient<IEnumerable<ArtifactBundle>>(_ => []);
        services.AddSingleton<EntityStore<ArtifactBundle>>();
        services.AddTransient<ArtifactBundleService>();
        services.AddTransient<IEnumerable<Job>>(_ => []);
        services.AddSingleton<EntityStore<Job>>();
        services.AddTransient<IEnumerable<JobGroup>>(_ => []);
        services.AddSingleton<EntityStore<JobGroup>>();
        services.AddSingleton<IdProvider>();
        services.AddSingleton<JobEvents>();
        services.AddTransient<JobService>();
        services.AddTransient<JobGroupService>();
        services.AddTransient<JobLogService>();
        services.AddTransient<JobObsoletionService>();
        services.AddTransient<JobCancellationService>();
        services.AddTransient<JobGroupCompletionService>();
        services.AddTransient<DownstreamTriggerService>();
        services.AddSingleton<NoOpJobExecutorPendingStore>();
        services.TryAddTransient<IJobExecutor, NoOpJobExecutor>();

        // Registry must be registered before JobPersistenceService so IHostedLifecycleService
        // reverse-order StoppingAsync drains live watchers before persistence flushes.
        services.AddSingleton<JobWatcherRegistry>();
        services.AddSingleton<IHostedLifecycleService>(sp => sp.GetRequiredService<JobWatcherRegistry>());
        services.AddHostedService<JobRunner>();
        services.AddTransient<JobQueueService>();
        services.AddSingleton<IRunOnStartup, JobEventRegistration>();
        services.AddHostedService<StartupRunner>();
        services.AddTransient<PipelineService>();
        services.AddTransient<PipelineSummaryService>();
        services.AddTransient<PipelineDocumentBuilder>();
        services.AddTransient<PipelineDocumentCreator>();
        services.AddTransient<ManifestCompiler>();
        services.AddTransient<PipelineReconciler>();
        services.AddSingleton<ReconcilePauseState>();
        services.AddSingleton<ReconcileOptions>();
        services.AddTransient<ReconcileCoordinator>();
        services.AddSingleton<IPersistenceReadiness, PersistenceReadiness>();
        services.AddHostedService<ConfigurationPersistenceService>();
        services.AddHostedService<PromotionPersistenceService>();
        services.AddHostedService<BundlePersistenceService>();
        services.AddHostedService<JobPersistenceService>();
    }
}
