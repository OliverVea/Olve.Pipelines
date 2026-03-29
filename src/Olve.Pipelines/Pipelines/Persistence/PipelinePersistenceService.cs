using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Pipelines.Persistence;

public class PipelinePersistenceService(
    EntityStore<Pipeline> store,
    StorageOptions storageOptions,
    ILogger<PipelinePersistenceService> logger,
    IAmazonS3? s3 = null) : IHostedLifecycleService
{
    private const string Key = "pipelines.json";

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (s3 is null)
        {
            logger.LogWarning("S3 not configured, skipping pipeline save");
            return;
        }

        var snapshot = store.List();

        var data = snapshot
            .Select(p => new PipelinePersistedData(p.Id.Value.Value, p.Name))
            .ToArray();

        var json = JsonSerializer.SerializeToUtf8Bytes(
            data,
            PipelinePersistenceJsonContext.Default.PipelinePersistedDataArray);

        var request = new PutObjectRequest
        {
            BucketName = storageOptions.Bucket,
            Key = Key,
            InputStream = new MemoryStream(json),
            ContentType = "application/json",
        };

        await s3.PutObjectAsync(request, cancellationToken);

        logger.LogInformation("Saved {Count} pipelines to S3", data.Length);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public record PipelinePersistedData(Guid Id, string Name);
