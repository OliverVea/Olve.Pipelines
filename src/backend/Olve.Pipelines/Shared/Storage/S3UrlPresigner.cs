using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Shared.Storage;

/// <summary>
/// Pre-signing settings. <paramref name="InClusterEndpoint"/> is the Garage endpoint as step pods reach
/// it (<c>Storage:InClusterEndpoint</c>, default <c>Storage:Endpoint</c>) — its host is baked into the
/// signature, so it must be exactly what the pod dials.
/// </summary>
/// <param name="DefaultExpiry">Expiry used when a caller passes none (<c>Storage:PresignExpirySeconds</c>, default 3600).</param>
public sealed record PresignOptions(Uri InClusterEndpoint, string Bucket, string Region, TimeSpan DefaultExpiry);

/// <summary>
/// Pre-signs GET/PUT URLs for objects in the pipelines bucket, against the in-cluster endpoint, with the
/// service's current S3 credentials. URLs embed the access key id: treat them as secrets (never log them
/// past <c>?</c>; hand them to pods via a K8s Secret).
/// </summary>
public sealed class S3UrlPresigner(
    PresignOptions options,
    ICredentialsProvider<S3Credentials> credentialsProvider,
    TimeProvider timeProvider)
{
    public PresignOptions Options => options;

    public Task<string> PresignGetAsync(string key, TimeSpan? expiry = null, CancellationToken ct = default)
        => PresignAsync("GET", key, expiry, ct);

    public Task<string> PresignPutAsync(string key, TimeSpan? expiry = null, CancellationToken ct = default)
        => PresignAsync("PUT", key, expiry, ct);

    private async Task<string> PresignAsync(string method, string key, TimeSpan? expiry, CancellationToken ct)
    {
        // STS credentials (OIDC mode) expire on their own; a URL can't outlive them whatever X-Amz-Expires says.
        // v2 deployments use a static Garage key, where this is moot.
        var credentials = await credentialsProvider.GetCredentialsAsync(ct);
        return SigV4Presigner.PresignPathStyle(
            method, options.InClusterEndpoint, options.Bucket, key, credentials, options.Region,
            timeProvider.GetUtcNow(), expiry ?? options.DefaultExpiry);
    }
}

public static class StoragePresignConfiguration
{
    /// <summary>
    /// Registers <see cref="PresignOptions"/> and <see cref="S3UrlPresigner"/>. Call after the storage
    /// credentials provider is registered (only then can anything be signed).
    /// </summary>
    public static void AddStoragePresigning(this IServiceCollection services, IConfiguration configuration, string endpoint, string bucket)
    {
        var inCluster = configuration["Storage:InClusterEndpoint"];
        if (string.IsNullOrWhiteSpace(inCluster)) inCluster = endpoint;
        var region = configuration["Storage:Region"];
        if (string.IsNullOrWhiteSpace(region)) region = "us-east-1";
        var expirySeconds = configuration.GetValue<int?>("Storage:PresignExpirySeconds") ?? 3600;

        services.AddSingleton(new PresignOptions(new Uri(inCluster), bucket, region, TimeSpan.FromSeconds(expirySeconds)));
        services.AddSingleton<S3UrlPresigner>();
    }
}
