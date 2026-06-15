using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// I/O seam for whole-state snapshot persistence. Reads return <c>null</c> when nothing is
/// stored yet (first run); any other read failure throws so callers can fail loudly rather than
/// overwrite good state with empty. Lets the persistence services be unit-tested without a real
/// S3 client (fake the store to return data, return null, or throw).
/// </summary>
public interface ISnapshotStore
{
    /// <summary>Reads the object at <paramref name="key"/>, or <c>null</c> if it does not exist.</summary>
    Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="content"/> to <paramref name="key"/>.</summary>
    Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken);
}

public sealed class S3SnapshotStore(IAmazonS3 s3, StorageOptions options) : ISnapshotStore
{
    public async Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await s3.GetObjectAsync(options.Bucket, key, cancellationToken);
            await using var stream = response.ResponseStream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task WriteAsync(string key, byte[] content, CancellationToken cancellationToken) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = new MemoryStream(content),
            ContentType = "application/json",
        }, cancellationToken);
}
