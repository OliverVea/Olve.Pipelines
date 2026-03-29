using Olve.Pipelines.PipelineArtifacts;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Persistence;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Configuration;

public static class ServiceConfiguration
{
    public static void AddPipelineServices(this IServiceCollection services)
    {
        services.AddTransient<IEnumerable<Pipeline>>(_ => DevelopmentPipelineSeeder.GetPipelines());
        services.AddTransient<IEnumerable<PipelineSource>>(_ => DevelopmentPipelineSeeder.GetPipelineSources());
        services.AddTransient<IEnumerable<PipelineBuilder>>(_ => DevelopmentPipelineSeeder.GetPipelineBuilders());
        services.AddTransient<IEnumerable<PipelineArtifact>>(_ => DevelopmentPipelineSeeder.GetPipelineArtifacts());
        services.AddTransient<IEnumerable<ProcessingStep>>(_ => DevelopmentPipelineSeeder.GetProcessingSteps());
        services.AddTransient<IEnumerable<Verification>>(_ => DevelopmentPipelineSeeder.GetVerifications());
        services.AddSingleton<EntityStore<Pipeline>>();
        services.AddSingleton<EntityStore<PipelineSource>>();
        services.AddSingleton<EntityStore<PipelineBuilder>>();
        services.AddSingleton<EntityStore<PipelineArtifact>>();
        services.AddSingleton<EntityStore<ProcessingStep>>();
        services.AddSingleton<EntityStore<Verification>>();
        services.AddScoped<PipelineService>();
        services.AddScoped<PipelineSourceService>();
        services.AddScoped<PipelineBuilderService>();
        services.AddScoped<PipelineArtifactService>();
        services.AddScoped<ProcessingStepService>();
        services.AddScoped<VerificationService>();
        services.AddHostedService<PipelinePersistenceService>();
    }
}
