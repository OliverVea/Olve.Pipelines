using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes;

public static class KubernetesConfiguration
{
    public static void ConfigureKubernetes(this WebApplicationBuilder builder)
    {
        var s3HelperImage = builder.Configuration["Kubernetes:S3HelperImage"] ?? "minio/mc";
        var s3Bucket = builder.Configuration["Storage:Bucket"] ?? "olve-pipelines";
        var s3Endpoint = builder.Configuration["Storage:Endpoint"] ?? "";
        var s3SkipCert = builder.Configuration.GetValue<bool>("Storage:SkipCertValidation");
        var defaultImage = builder.Configuration["Kubernetes:DefaultImage"] ?? "alpine:latest";
        var configNs = builder.Configuration["Kubernetes:Namespace"];
        // Empty/absent = no runtimeClassName on job pods (plain runc) — the rollback path.
        var runtimeClassName = builder.Configuration["Kubernetes:RuntimeClassName"];
        if (string.IsNullOrWhiteSpace(runtimeClassName)) runtimeClassName = null;

        // InCluster (Tier-A): reach the cluster API via the pod's own ServiceAccount — no
        // OpenBao/Authentik. Opt-in via config so the legacy path below stays the default
        // for local dev / tests / prod (which sets AuthMode=OpenBao explicitly).
        if (string.Equals(builder.Configuration["Kubernetes:AuthMode"], "InCluster", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureInCluster(builder, configNs, defaultImage, s3HelperImage, s3Bucket, s3Endpoint, s3SkipCert, runtimeClassName);
            return;
        }

        var openBaoUrl = builder.Configuration["Kubernetes:OpenBaoUrl"];
        var authUrl = builder.Configuration["Storage:AuthUrl"];
        var clientId = builder.Configuration["Storage:ClientId"];
        var clientSecret = builder.Configuration["Storage:ClientSecret"];

        if (openBaoUrl is null || authUrl is null || clientId is null || clientSecret is null)
        {
            builder.Services.AddSingleton(new KubernetesOptions("", "", s3HelperImage, s3Bucket, s3Endpoint, s3SkipCert));
            builder.Services.AddSingleton<KubernetesClient>(sp =>
                throw new InvalidOperationException("Kubernetes is not configured."));
            builder.Services.AddSingleton<IKubernetesClient>(sp => sp.GetRequiredService<KubernetesClient>());
            return;
        }

        var skipCertValidation = builder.Configuration.GetValue<bool>("Storage:SkipCertValidation");

        var tokenProvider = new OAuth2TokenProvider(
            authUrl, clientId, clientSecret,
            scope: "openid profile email",
            skipCertValidation: skipCertValidation);

        var openBaoClient = new OpenBaoClient(
            tokenProvider,
            openBaoUrl,
            skipCertValidation: skipCertValidation,
            // Bind to the app's Logging config (not a bare AddConsole) so this SDK client honors the
            // configured console formatter — emitting JSON in deployed envs instead of polluting the
            // structured stdout stream with plain-text lines.
            logger: LoggerFactory.Create(b => b
                .AddConfiguration(builder.Configuration.GetSection("Logging"))
                .AddConsole()).CreateLogger<OpenBaoClient>());

        var credentialsProvider = new OpenBaoCredentialsProvider(openBaoClient);

        builder.Services.AddSingleton(openBaoClient);
        builder.Services.AddSingleton<ICredentialsProvider<KubernetesCredentials>>(credentialsProvider);

        builder.Services.AddSingleton(sp =>
        {
            var credentials = sp.GetRequiredService<ICredentialsProvider<KubernetesCredentials>>()
                .GetCredentialsAsync().GetAwaiter().GetResult();

            sp.GetRequiredService<ILogger<KubernetesClient>>()
                .LogInformation("Kubernetes configured: server={Server}, namespace={Namespace}",
                    credentials.Server, configNs ?? credentials.Namespace);

            return new KubernetesOptions(configNs ?? credentials.Namespace, defaultImage, s3HelperImage, s3Bucket, s3Endpoint, s3SkipCert, runtimeClassName);
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

    private static void ConfigureInCluster(
        WebApplicationBuilder builder,
        string? configNs,
        string defaultImage,
        string s3HelperImage,
        string s3Bucket,
        string s3Endpoint,
        bool s3SkipCert,
        string? runtimeClassName)
    {
        builder.Services.AddSingleton<ICredentialsProvider<KubernetesCredentials>>(new InClusterCredentialsProvider());

        builder.Services.AddSingleton(sp =>
        {
            var credentials = sp.GetRequiredService<ICredentialsProvider<KubernetesCredentials>>()
                .GetCredentialsAsync().GetAwaiter().GetResult();

            sp.GetRequiredService<ILogger<KubernetesClient>>()
                .LogInformation("Kubernetes configured (InCluster): server={Server}, namespace={Namespace}",
                    credentials.Server, configNs ?? credentials.Namespace);

            return new KubernetesOptions(configNs ?? credentials.Namespace, defaultImage, s3HelperImage, s3Bucket, s3Endpoint, s3SkipCert, runtimeClassName);
        });

        builder.Services.AddSingleton(sp =>
        {
            var credentials = sp.GetRequiredService<ICredentialsProvider<KubernetesCredentials>>()
                .GetCredentialsAsync().GetAwaiter().GetResult();
            return new KubernetesClient(
                credentials,
                sp.GetRequiredService<ILogger<KubernetesClient>>(),
                InClusterCredentialsProvider.ReadTokenAsync);
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
    bool S3SkipCertValidation,
    string? RuntimeClassName = null);
