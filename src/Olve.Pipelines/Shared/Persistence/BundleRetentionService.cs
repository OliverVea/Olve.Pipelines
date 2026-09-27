using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Utilities.Stores;

namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Periodically applies <see cref="BundleRetentionPolicy"/> to the bucket: lists <c>bundles/</c>,
/// and deletes every object of each expired bundle (outputs + logs together). Best-effort — a
/// failed sweep is logged and retried next interval; it never affects jobs or persistence.
/// </summary>
public class BundleRetentionService(
    IAmazonS3 s3,
    StorageOptions storageOptions,
    BundleRetentionOptions options,
    EntityStore<Pipeline> pipelines,
    TimeProvider time,
    ILogger<BundleRetentionService> logger) : BackgroundService
{
    private const int DeleteBatchSize = 1000;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Enabled || storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Bundle retention disabled");
            return;
        }

        logger.LogInformation(
            "Bundle retention: keeping each pipeline's latest {KeepLatest} bundles and anything newer than {MinAgeDays} days",
            options.KeepLatest, options.MinAge.TotalDays);

        try
        {
            await Task.Delay(InitialDelay, time, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Bundle retention sweep failed; retrying next interval");
                }

                await Task.Delay(options.Interval, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var objects = new List<(string Key, DateTimeOffset LastModified)>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = storageOptions.Bucket,
                Prefix = "bundles/",
                ContinuationToken = token,
            }, ct);

            foreach (var obj in page.S3Objects ?? [])
            {
                if (obj.LastModified is { } modified)
                    objects.Add((obj.Key, new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero)));
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);

        var bundles = BundleRetentionPolicy.GroupObjects(objects);
        var expired = BundleRetentionPolicy.SelectExpired(bundles, time.GetUtcNow(), options.KeepLatest, options.MinAge);

        if (expired.Count == 0)
        {
            logger.LogInformation("Bundle retention sweep: {BundleCount} bundles, none expired", bundles.Count);
            return;
        }

        var names = pipelines.List().ToDictionary(p => p.Id.Value.Value.ToString("N"), p => p.Name);
        foreach (var bundle in expired)
        {
            foreach (var batch in bundle.ObjectKeys.Chunk(DeleteBatchSize))
            {
                var response = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = storageOptions.Bucket,
                    Objects = batch.Select(k => new KeyVersion { Key = k }).ToList(),
                }, ct);

                if (response.DeleteErrors is { Count: > 0 } errors)
                {
                    logger.LogWarning("Failed to delete {ErrorCount} objects of expired bundle '{Prefix}': {FirstError}",
                        errors.Count, bundle.Prefix, errors[0].Message);
                }
            }

            logger.LogInformation(
                "Deleted expired bundle '{Prefix}' of pipeline '{PipelineName}' ({ObjectCount} objects, last activity {LastActivity:o})",
                bundle.Prefix, names.GetValueOrDefault(bundle.PipelineKey, "(deleted pipeline)"), bundle.ObjectKeys.Count, bundle.LastActivity);
        }

        logger.LogInformation("Bundle retention sweep: {BundleCount} bundles, {ExpiredCount} expired and deleted",
            bundles.Count, expired.Count);
    }
}
