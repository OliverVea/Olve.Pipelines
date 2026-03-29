using Olve.Pipelines.Building;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Shared.Persistence;
using Olve.Pipelines.Sourcing;

namespace Olve.Pipelines.Configuration;

public static class ServiceConfiguration
{
    public static void AddPipelineServices(this IServiceCollection services)
    {
        services.AddTransient<IEnumerable<Pipeline>>(_ => DevelopmentPipelineSeeder.GetPipelines());
        services.AddTransient<IEnumerable<PipelineSource>>(_ => DevelopmentPipelineSeeder.GetPipelineSources());
        services.AddTransient<IEnumerable<PipelineBuilder>>(_ => DevelopmentPipelineSeeder.GetPipelineBuilders());
        services.AddTransient<IEnumerable<ProcessingStep>>(_ => DevelopmentPipelineSeeder.GetProcessingSteps());
        services.AddTransient<IEnumerable<Verification>>(_ => DevelopmentPipelineSeeder.GetVerifications());
        services.AddSingleton<EntityStore<Pipeline>>();
        services.AddSingleton<EntityStore<PipelineSource>>();
        services.AddSingleton<EntityStore<PipelineBuilder>>();
        services.AddSingleton<EntityStore<ProcessingStep>>();
        services.AddSingleton<EntityStore<Verification>>();
        services.AddTransient<IEnumerable<SourceBundle>>(_ => []);
        services.AddTransient<IEnumerable<ArtifactBundle>>(_ => []);
        services.AddSingleton<EntityStore<SourceBundle>>();
        services.AddSingleton<EntityStore<ArtifactBundle>>();
        services.AddSingleton<AttachmentStore<PipelineSource, HardcodedSource>>();
        services.AddSingleton<AttachmentStore<PipelineSource, GitHubSource>>();
        services.AddSingleton<AttachmentStore<PipelineBuilder, ScriptBuilder>>();
        services.AddSingleton<AttachmentStore<ProcessingStep, ScriptProcessing>>();
        services.AddSingleton<AttachmentStore<Verification, ScriptVerification>>();
        services.AddScoped<PipelineService>();
        services.AddScoped<PipelineSourceService>();
        services.AddScoped<PipelineBuilderService>();
        services.AddScoped<ProcessingStepService>();
        services.AddScoped<VerificationService>();
        services.AddScoped<SourceBundleService>();
        services.AddScoped<ArtifactBundleService>();
        services.AddHostedService<ConfigurationPersistenceService>();
        services.AddHostedService<BundlePersistenceService>();
    }
}
