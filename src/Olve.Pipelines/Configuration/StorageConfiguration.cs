using System.Reflection;
using Amazon.Runtime;
using Amazon.S3;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Configuration;

public static class StorageConfiguration
{
    public static void ConfigureStorage(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["Storage:Endpoint"];
        var bucket = builder.Configuration["Storage:Bucket"] ?? "olve-pipelines";
        var skipCertValidation = builder.Configuration.GetValue<bool>("Storage:SkipCertValidation");
        var mode = builder.Configuration.GetValue<StorageMode?>("Storage:Mode") ?? StorageMode.Persistent;

        // The build-time OpenAPI generator (dotnet-getdocument) boots the app to extract api.json. It
        // starts hosted services but has no storage config, which would trip the persistent-mode
        // fail-fast and break the build. Force Ephemeral so doc generation runs inert (no read/save).
        if (IsOpenApiDocumentGeneration())
        {
            mode = StorageMode.Ephemeral;
        }

        builder.Services.AddSingleton(new StorageOptions(bucket, skipCertValidation, mode));

        if (endpoint is null) return;

        // Static credentials (integration tests, local dev with MinIO)
        var accessKey = builder.Configuration["Storage:AccessKey"];
        var secretKey = builder.Configuration["Storage:SecretKey"];

        // OIDC → STS credentials (production with MinIO + Authentik)
        var authUrl = builder.Configuration["Storage:AuthUrl"];
        var clientId = builder.Configuration["Storage:ClientId"];
        var clientSecret = builder.Configuration["Storage:ClientSecret"];
        var roleArn = builder.Configuration["Storage:RoleArn"];

        ICredentialsProvider<S3Credentials>? credentialsProvider;
        if (accessKey is not null && secretKey is not null)
        {
            credentialsProvider = new DirectCredentialsProvider<S3Credentials>(
                new S3Credentials(accessKey, secretKey));
        }
        else if (authUrl is not null && clientId is not null && clientSecret is not null && roleArn is not null)
        {
            var tokenProvider = new OAuth2TokenProvider(
                authUrl, clientId, clientSecret,
                scope: builder.Configuration["Storage:Scope"] ?? "openid profile email minio",
                skipCertValidation: skipCertValidation);

            credentialsProvider = new StsCredentialsProvider(
                tokenProvider,
                stsEndpoint: endpoint,
                roleArn: roleArn,
                skipCertValidation: skipCertValidation,
                // Bind to the app's Logging config (not a bare AddConsole) so this SDK client honors the
                // configured console formatter — emitting JSON in deployed envs instead of polluting the
                // structured stdout stream with plain-text lines.
                logger: LoggerFactory.Create(b => b
                    .AddConfiguration(builder.Configuration.GetSection("Logging"))
                    .AddConsole()).CreateLogger<StsCredentialsProvider>());
        }
        else
        {
            return;
        }

        builder.Services.AddSingleton(credentialsProvider);

        var s3Config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
        };

        if (skipCertValidation)
        {
            s3Config.HttpClientFactory = new SkipCertValidationFactory();
        }

        builder.Services.AddSingleton<IAmazonS3>(sp =>
        {
            var provider = sp.GetRequiredService<ICredentialsProvider<S3Credentials>>();
            var awsCreds = new ProviderBackedAwsCredentials(provider);
            return new AmazonS3Client(awsCreds, s3Config);
        });

        builder.Services.AddSingleton<IBundleStore, S3BundleStore>();
        builder.Services.AddSingleton<ISnapshotStore, S3SnapshotStore>();
    }

    // dotnet-getdocument launches a nested "GetDocument.Insider" process that reflectively loads this
    // app and sets it as the entry assembly, so detect the insider host by name.
    private static bool IsOpenApiDocumentGeneration() =>
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider";

    private class SkipCertValidationFactory : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            return new HttpClient(handler);
        }
    }
}

public record StorageOptions(string Bucket, bool SkipCertValidation = false, StorageMode Mode = StorageMode.Persistent);

/// <summary>
/// How the app treats snapshot persistence.
/// <list type="bullet">
/// <item><see cref="Persistent"/> — storage is required; the app must load before becoming ready,
/// crashloops on load failure, and never overwrites good state with empty. The default.</item>
/// <item><see cref="Ephemeral"/> — in-memory only; no load, no save, ready immediately. An explicit
/// opt-in for local dev and throwaway runs.</item>
/// </list>
/// </summary>
public enum StorageMode
{
    Persistent,
    Ephemeral,
}

internal class ProviderBackedAwsCredentials(ICredentialsProvider<S3Credentials> provider) : AWSCredentials
{
    public override async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        var creds = await provider.GetCredentialsAsync();
        return new ImmutableCredentials(creds.AccessKey, creds.SecretKey, creds.SessionToken);
    }

    public override ImmutableCredentials GetCredentials() => GetCredentialsAsync().GetAwaiter().GetResult();
}
