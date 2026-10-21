using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// The Garage operations the harness executor, reconcile and trigger mapping need: small text objects
/// (input.json, logs, events), result collection, and the staged-binary copy. A seam so the executor is
/// testable without S3.
/// </summary>
public interface IHarnessStorage
{
    Task PutTextAsync(string key, string content, string contentType, CancellationToken ct = default);

    /// <summary>The object's text, or null when it does not exist.</summary>
    Task<string?> GetTextAsync(string key, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>See <see cref="HarnessResultCollector.CollectAsync"/>.</summary>
    Task<HarnessCollection> CollectAsync(Attempt attempt, CancellationToken ct = default);
}

public sealed class S3HarnessStorage(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    HarnessResultCollector collector,
    ILogger<S3HarnessStorage> logger) : IHarnessStorage
{
    public async Task PutTextAsync(string key, string content, string contentType, CancellationToken ct = default)
        => await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = storageOptions.Bucket,
            Key = key,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            ContentType = contentType,
        }, ct);

    public async Task<string?> GetTextAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var response = await s3.GetObjectAsync(storageOptions.Bucket, key, ct);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(storageOptions.Bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct = default)
        => await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = storageOptions.Bucket,
            SourceKey = sourceKey,
            DestinationBucket = storageOptions.Bucket,
            DestinationKey = destinationKey,
        }, ct);

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await s3.DeleteObjectAsync(storageOptions.Bucket, key, ct);
        }
        catch (AmazonS3Exception e)
        {
            logger.LogWarning(e, "Could not delete '{Key}'", key);
        }
    }

    public Task<HarnessCollection> CollectAsync(Attempt attempt, CancellationToken ct = default)
        => collector.CollectAsync(attempt, ct);
}
