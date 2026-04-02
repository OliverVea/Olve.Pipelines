using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Shared.Persistence;

public class S3BundleStore(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    ILogger<S3BundleStore> logger) : IBundleStore
{
    private const string ArtifactPrefix = "bundles/artifact/";
    private bool _bucketEnsured;

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketEnsured) return;

        try
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = storageOptions.Bucket }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }

        _bucketEnsured = true;
    }

    public async Task UploadArtifactBundleAsync(ArtifactBundle metadata, Stream content, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var data = new ArtifactBundlePersistedData(
            metadata.Id,
            metadata.PipelineId,
            metadata.CreatedAt);

        await UploadBundleAsync(
            $"{ArtifactPrefix}{metadata.Id.Value.Value}",
            data,
            BundlePersistenceJsonContext.Default.ArtifactBundlePersistedData,
            content,
            ct);

        logger.LogInformation("Uploaded artifact bundle {BundleId}", metadata.Id.Value.Value);
    }

    public async Task<Stream> DownloadArtifactBundleAsync(Id<ArtifactBundle> id, CancellationToken ct = default)
    {
        var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = storageOptions.Bucket,
            Key = $"{ArtifactPrefix}{id.Value.Value}.zip",
        }, ct);

        return response.ResponseStream;
    }

    public async Task<IReadOnlyList<ArtifactBundle>> ListArtifactBundlesAsync(CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var results = new List<ArtifactBundle>();

        await foreach (var data in ListMetadataAsync<ArtifactBundlePersistedData>(
            ArtifactPrefix,
            BundlePersistenceJsonContext.Default.ArtifactBundlePersistedData,
            ct))
        {
            var bundle = new ArtifactBundle(
                data.Id,
                data.PipelineId,
                data.CreatedAt,
                ArtifactBundleStatus.Completed);

            results.Add(bundle);
        }

        return results;
    }

    private async Task UploadBundleAsync<T>(
        string keyPrefix,
        T metadata,
        JsonTypeInfo<T> typeInfo,
        Stream content,
        CancellationToken ct)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, typeInfo);

        await Task.WhenAll(
            s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = $"{keyPrefix}.json",
                InputStream = new MemoryStream(jsonBytes),
                ContentType = "application/json",
            }, ct),
            s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = $"{keyPrefix}.zip",
                InputStream = content,
                ContentType = "application/zip",
            }, ct));
    }

    private async IAsyncEnumerable<T> ListMetadataAsync<T>(
        string prefix,
        JsonTypeInfo<T> typeInfo,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? continuationToken = null;

        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = storageOptions.Bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
            }, ct);

            foreach (var obj in response.S3Objects ?? [])
            {
                if (!obj.Key.EndsWith(".json"))
                    continue;

                var getResponse = await s3.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = storageOptions.Bucket,
                    Key = obj.Key,
                }, ct);

                var data = await JsonSerializer.DeserializeAsync(
                    getResponse.ResponseStream,
                    typeInfo,
                    ct);

                if (data is not null)
                    yield return data;
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);
    }
}
