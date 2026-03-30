using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Kubernetes;

public static class KubernetesConfiguration
{
    public static void ConfigureKubernetes(this WebApplicationBuilder builder)
    {
        var openBaoUrl = builder.Configuration["Kubernetes:OpenBaoUrl"];
        var authUrl = builder.Configuration["Storage:AuthUrl"];
        var clientId = builder.Configuration["Storage:ClientId"];
        var clientSecret = builder.Configuration["Storage:ClientSecret"];

        if (openBaoUrl is null || authUrl is null || clientId is null || clientSecret is null)
        {
            builder.Services.AddSingleton<JobTracker>();
            builder.Services.AddSingleton(new KubernetesOptions("", ""));
            builder.Services.AddSingleton<KubernetesClient>(sp =>
                throw new InvalidOperationException("Kubernetes is not configured."));
            builder.Services.AddScoped(sp => new JobRunnerService(
                null,
                "",
                "",
                sp.GetRequiredService<JobTracker>(),
                sp.GetRequiredService<PipelineSources.PipelineSourceService>(),
                sp.GetRequiredService<PipelineBuilders.PipelineBuilderService>(),
                sp.GetRequiredService<Processing.ProcessingStepService>(),
                sp.GetRequiredService<Sourcing.SourceBundleService>(),
                sp.GetRequiredService<Building.ArtifactBundleService>(),
                sp.GetRequiredService<ILogger<JobRunnerService>>()));
            return;
        }

        var defaultImage = builder.Configuration["Kubernetes:DefaultImage"] ?? "alpine:latest";
        var skipCertValidation = builder.Configuration.GetValue<bool>("Storage:SkipCertValidation");

        var tokenProvider = new OAuth2TokenProvider(
            authUrl, clientId, clientSecret,
            scope: "openid profile email",
            skipCertValidation: skipCertValidation);

        var openBaoClient = new OpenBaoClient(
            tokenProvider,
            openBaoUrl,
            skipCertValidation: skipCertValidation,
            logger: LoggerFactory.Create(b => b.AddConsole()).CreateLogger<OpenBaoClient>());

        builder.Services.AddSingleton(openBaoClient);
        builder.Services.AddSingleton<JobTracker>();

        builder.Services.AddSingleton(sp =>
        {
            var credentials = openBaoClient.GetCredentialsAsync().GetAwaiter().GetResult();
            var configNs = builder.Configuration["Kubernetes:Namespace"];

            sp.GetRequiredService<ILogger<KubernetesClient>>()
                .LogInformation("Kubernetes configured: server={Server}, namespace={Namespace}",
                    credentials.Server, configNs ?? credentials.Namespace);

            return new KubernetesOptions(configNs ?? credentials.Namespace, defaultImage);
        });

        builder.Services.AddSingleton(sp =>
        {
            var credentials = openBaoClient.GetCredentialsAsync().GetAwaiter().GetResult();
            return new KubernetesClient(
                credentials,
                sp.GetRequiredService<ILogger<KubernetesClient>>());
        });

        builder.Services.AddScoped(sp =>
        {
            var options = sp.GetRequiredService<KubernetesOptions>();
            return new JobRunnerService(
                sp.GetRequiredService<KubernetesClient>(),
                options.Namespace,
                options.DefaultImage,
                sp.GetRequiredService<JobTracker>(),
                sp.GetRequiredService<PipelineSources.PipelineSourceService>(),
                sp.GetRequiredService<PipelineBuilders.PipelineBuilderService>(),
                sp.GetRequiredService<Processing.ProcessingStepService>(),
                sp.GetRequiredService<Sourcing.SourceBundleService>(),
                sp.GetRequiredService<Building.ArtifactBundleService>(),
                sp.GetRequiredService<ILogger<JobRunnerService>>());
        });

    }
}

public record KubernetesOptions(string Namespace, string DefaultImage);
