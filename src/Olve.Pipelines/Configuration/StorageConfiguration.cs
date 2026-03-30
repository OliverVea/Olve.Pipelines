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

        builder.Services.AddSingleton(new StorageOptions(bucket, skipCertValidation));

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
                logger: LoggerFactory.Create(b => b.AddConsole()).CreateLogger<StsCredentialsProvider>());
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
    }

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

public record StorageOptions(string Bucket, bool SkipCertValidation = false);

internal class ProviderBackedAwsCredentials(ICredentialsProvider<S3Credentials> provider) : AWSCredentials
{
    public override async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        var creds = await provider.GetCredentialsAsync();
        return new ImmutableCredentials(creds.AccessKey, creds.SecretKey, creds.SessionToken);
    }

    public override ImmutableCredentials GetCredentials() => GetCredentialsAsync().GetAwaiter().GetResult();
}
