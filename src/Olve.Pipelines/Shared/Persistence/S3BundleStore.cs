using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Building;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Sourcing;

namespace Olve.Pipelines.Shared.Persistence;

public class S3BundleStore(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    ILogger<S3BundleStore> logger) : IBundleStore
{
    private const string SourcePrefix = "bundles/source/";
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
            // Bucket already exists, that's fine
        }

        _bucketEnsured = true;
    }

    public async Task UploadSourceBundleAsync(SourceBundle metadata, Stream content, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var data = new SourceBundlePersistedData(
            metadata.Id,
            metadata.PipelineId,
            metadata.CreatedAt);

        await UploadBundleAsync(
            $"{SourcePrefix}{metadata.Id.Value.Value}",
            data,
            BundlePersistenceJsonContext.Default.SourceBundlePersistedData,
            content,
            ct);

        logger.LogInformation("Uploaded source bundle {BundleId}", metadata.Id.Value.Value);
    }

    public async Task UploadArtifactBundleAsync(ArtifactBundle metadata, Stream content, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var data = new ArtifactBundlePersistedData(
            metadata.Id,
            metadata.PipelineId,
            metadata.SourceBundleId,
            metadata.CreatedAt);

        await UploadBundleAsync(
            $"{ArtifactPrefix}{metadata.Id.Value.Value}",
            data,
            BundlePersistenceJsonContext.Default.ArtifactBundlePersistedData,
            content,
            ct);

        logger.LogInformation("Uploaded artifact bundle {BundleId}", metadata.Id.Value.Value);
    }

    public async Task<Stream> DownloadSourceBundleAsync(Id<SourceBundle> id, CancellationToken ct = default)
    {
        var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = storageOptions.Bucket,
            Key = $"{SourcePrefix}{id.Value.Value}.zip",
        }, ct);

        return response.ResponseStream;
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

    public async Task<IReadOnlyList<SourceBundle>> ListSourceBundlesAsync(CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var results = new List<SourceBundle>();

        await foreach (var data in ListMetadataAsync<SourceBundlePersistedData>(
            SourcePrefix,
            BundlePersistenceJsonContext.Default.SourceBundlePersistedData,
            ct))
        {
            var bundle = new SourceBundle(
                data.Id,
                data.PipelineId,
                data.CreatedAt,
                SourceBundleStatus.Completed);

            results.Add(bundle);
        }

        return results;
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
                data.SourceBundleId,
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
