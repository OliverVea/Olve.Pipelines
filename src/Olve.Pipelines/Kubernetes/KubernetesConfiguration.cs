using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes;

public static class KubernetesConfiguration
{
    public static void ConfigureKubernetes(this WebApplicationBuilder builder)
    {
        var openBaoUrl = builder.Configuration["Kubernetes:OpenBaoUrl"];
        var authUrl = builder.Configuration["Storage:AuthUrl"];
        var clientId = builder.Configuration["Storage:ClientId"];
        var clientSecret = builder.Configuration["Storage:ClientSecret"];

        var s3HelperImage = builder.Configuration["Kubernetes:S3HelperImage"] ?? "minio/mc";
        var s3Bucket = builder.Configuration["Storage:Bucket"] ?? "olve-pipelines";
        var s3Endpoint = builder.Configuration["Storage:Endpoint"] ?? "";
        var s3SkipCert = builder.Configuration.GetValue<bool>("Storage:SkipCertValidation");

        if (openBaoUrl is null || authUrl is null || clientId is null || clientSecret is null)
        {
            builder.Services.AddSingleton(new KubernetesOptions("", "", s3HelperImage, s3Bucket, s3Endpoint, s3SkipCert));
            builder.Services.AddSingleton<KubernetesClient>(sp =>
                throw new InvalidOperationException("Kubernetes is not configured."));
            builder.Services.AddSingleton<IKubernetesClient>(sp => sp.GetRequiredService<KubernetesClient>());
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

        var credentialsProvider = new OpenBaoCredentialsProvider(openBaoClient);

        builder.Services.AddSingleton(openBaoClient);
        builder.Services.AddSingleton<ICredentialsProvider<KubernetesCredentials>>(credentialsProvider);

        builder.Services.AddSingleton(sp =>
        {
            var credentials = sp.GetRequiredService<ICredentialsProvider<KubernetesCredentials>>()
                .GetCredentialsAsync().GetAwaiter().GetResult();
            var configNs = builder.Configuration["Kubernetes:Namespace"];

            sp.GetRequiredService<ILogger<KubernetesClient>>()
                .LogInformation("Kubernetes configured: server={Server}, namespace={Namespace}",
                    credentials.Server, configNs ?? credentials.Namespace);

            return new KubernetesOptions(configNs ?? credentials.Namespace, defaultImage, s3HelperImage, s3Bucket, s3Endpoint, s3SkipCert);
        });

        builder.Services.AddSingleton(sp =>
        {
            var credentials = sp.GetRequiredService<ICredentialsProvider<KubernetesCredentials>>()
                .GetCredentialsAsync().GetAwaiter().GetResult();
            return new KubernetesClient(
                credentials,
                sp.GetRequiredService<ILogger<KubernetesClient>>());
        });
        builder.Services.AddSingleton<IKubernetesClient>(sp => sp.GetRequiredService<KubernetesClient>());

        builder.Services.AddTransient<IPipelineSecretStore, KubernetesPipelineSecretStore>();
        builder.Services.AddTransient<IJobExecutor, KubernetesJobExecutor>();
    }
}

public record KubernetesOptions(
    string Namespace,
    string DefaultImage,
    string S3HelperImage,
    string S3Bucket,
    string S3Endpoint,
    bool S3SkipCertValidation);
