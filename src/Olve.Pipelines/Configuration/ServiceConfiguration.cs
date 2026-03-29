using Olve.Pipelines.PipelineArtifacts;
using Olve.Pipelines.PipelineBuilds;
using Olve.Pipelines.PipelineProcessing;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Persistence;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Configuration;

public static class ServiceConfiguration
{
    public static void AddPipelineServices(this IServiceCollection services)
    {
        services.AddTransient<IEnumerable<Pipeline>>(_ => DevelopmentPipelineSeeder.GetPipelines());
        services.AddTransient<IEnumerable<PipelineSource>>(_ => DevelopmentPipelineSeeder.GetPipelineSources());
        services.AddTransient<IEnumerable<PipelineBuild>>(_ => DevelopmentPipelineSeeder.GetPipelineBuilds());
        services.AddTransient<IEnumerable<PipelineArtifact>>(_ => DevelopmentPipelineSeeder.GetPipelineArtifacts());
        services.AddTransient<IEnumerable<PipelineProcessingStep>>(_ => DevelopmentPipelineSeeder.GetPipelineProcessing());
        services.AddTransient<IEnumerable<PipelineVerification>>(_ => DevelopmentPipelineSeeder.GetPipelineVerifications());
        services.AddSingleton<EntityStore<Pipeline>>();
        services.AddSingleton<EntityStore<PipelineSource>>();
        services.AddSingleton<EntityStore<PipelineBuild>>();
        services.AddSingleton<EntityStore<PipelineArtifact>>();
        services.AddSingleton<EntityStore<PipelineProcessingStep>>();
        services.AddSingleton<EntityStore<PipelineVerification>>();
        services.AddScoped<PipelineService>();
        services.AddScoped<PipelineSourceService>();
        services.AddScoped<PipelineBuildService>();
        services.AddScoped<PipelineArtifactService>();
        services.AddScoped<PipelineProcessingService>();
        services.AddScoped<PipelineVerificationService>();
        services.AddHostedService<PipelinePersistenceService>();
    }
}
