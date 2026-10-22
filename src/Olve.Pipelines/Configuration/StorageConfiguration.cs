using System.Reflection;
using Amazon.Runtime;
using Amazon.S3;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Pipelines.Shared.Persistence;
using Olve.Pipelines.Shared.Storage;

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

        // Static credentials: the instance's Garage access key (local dev and tests: any S3 store).
        var accessKey = builder.Configuration["Storage:AccessKey"];
        var secretKey = builder.Configuration["Storage:SecretKey"];
        if (accessKey is null || secretKey is null) return;

        ICredentialsProvider<S3Credentials> credentialsProvider =
            new DirectCredentialsProvider<S3Credentials>(new S3Credentials(accessKey, secretKey));

        builder.Services.AddSingleton(credentialsProvider);
        builder.Services.AddStoragePresigning(builder.Configuration, endpoint, bucket);

        var s3Config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            // SDK v4 defaults to aws-chunked uploads with a CRC trailer, which Garage rejects
            // ("Invalid payload signature"). Only send/validate checksums when an API requires them.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
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

        builder.Services.AddSingleton<ISnapshotStore, S3SnapshotStore>();
        builder.Services.AddSingleton<IRetentionObjectStore, S3RetentionObjectStore>();
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
