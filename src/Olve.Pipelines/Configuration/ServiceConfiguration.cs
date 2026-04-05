using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
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
        services.AddSingleton<ProcessingStepEvents>();
        services.AddSingleton<IRunOnStartup, ProcessingStepEventRegistration>();
        services.AddTransient<ProcessingStepService>();
        services.AddTransient<ProcessingStepCleanupService>();
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
        services.TryAddTransient<IJobExecutor, NoOpJobExecutor>();
        services.AddHostedService<JobRunner>();
        services.AddTransient<JobQueueService>();
        services.AddSingleton<IRunOnStartup, JobEventRegistration>();
        services.AddHostedService<StartupRunner>();
        services.AddTransient<PipelineService>();
        services.AddHostedService<ConfigurationPersistenceService>();
        services.AddHostedService<BundlePersistenceService>();
    }
}
