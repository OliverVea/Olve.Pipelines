using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Pipelines.Retention;

/// <summary>Bucket listing and bulk delete for retention (seam so the sweep is testable without S3).</summary>
public interface IRetentionObjectStore
{
    Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct);

    /// <summary>Deletes the keys; returns how many could not be deleted.</summary>
    Task<int> DeleteAsync(IReadOnlyList<string> keys, CancellationToken ct);
}

public sealed class S3RetentionObjectStore(IAmazonS3 s3, StorageOptions storage, ILogger<S3RetentionObjectStore> logger) : IRetentionObjectStore
{
    private const int DeleteBatchSize = 1000;

    public async Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct)
    {
        var objects = new List<StoredObject>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = storage.Bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);

            foreach (var o in page.S3Objects ?? [])
            {
                if (o.LastModified is { } modified)
                    objects.Add(new StoredObject(o.Key, new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero), o.Size ?? 0));
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);

        return objects;
    }

    public async Task<int> DeleteAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var failed = 0;
        foreach (var batch in keys.Chunk(DeleteBatchSize))
        {
            var response = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = storage.Bucket,
                Objects = batch.Select(k => new KeyVersion { Key = k }).ToList(),
            }, ct);

            if (response.DeleteErrors is { Count: > 0 } errors)
            {
                failed += errors.Count;
                logger.LogWarning("Failed to delete {ErrorCount} objects; first: '{Key}': {Message}", errors.Count, errors[0].Key, errors[0].Message);
            }
        }

        return failed;
    }
}
