using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Retention;

namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>The target's Garage bucket, with the client settings production uses.</summary>
public sealed class TestBucket
{
    public TestBucket(StorageTarget target)
    {
        Name = target.Bucket;
        Target = target;
        Client = new AmazonS3Client(new BasicAWSCredentials(target.AccessKey, target.SecretKey), new AmazonS3Config
        {
            ServiceURL = target.Endpoint,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
        Objects = new S3RetentionObjectStore(Client, new StorageOptions(Name), NullLogger<S3RetentionObjectStore>.Instance);
    }

    public string Name { get; }
    public StorageTarget Target { get; }
    public IAmazonS3 Client { get; }
    public S3RetentionObjectStore Objects { get; }

    public Task PutAsync(string key, string content) =>
        Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Name,
            Key = key,
            ContentBody = content,
            UseChunkEncoding = false,
        });

    public Task<IReadOnlyList<StoredObject>> ListAsync(string prefix) => Objects.ListAsync(prefix, CancellationToken.None);
}
