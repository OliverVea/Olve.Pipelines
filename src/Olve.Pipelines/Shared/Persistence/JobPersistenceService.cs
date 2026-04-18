using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;

namespace Olve.Pipelines.Shared.Persistence;

public class JobPersistenceService(
    EntityStore<Job> jobs,
    EntityStore<JobGroup> jobGroups,
    StorageOptions storageOptions,
    ILogger<JobPersistenceService> logger,
    IAmazonS3? s3 = null) : IHostedLifecycleService, IDisposable
{
    private const string Key = "jobs.json";

    private volatile bool _dirty;
    private volatile bool _loading;
    private Timer? _timer;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (s3 is null)
        {
            logger.LogWarning("S3 not configured, skipping job load");
            return;
        }

        try
        {
            var response = await s3.GetObjectAsync(storageOptions.Bucket, Key, cancellationToken);
            await using var stream = response.ResponseStream;

            var snapshot = await JsonSerializer.DeserializeAsync(
                stream,
                JobPersistenceJsonContext.Default.JobSnapshot,
                cancellationToken);

            if (snapshot is null) return;

            _loading = true;
            try
            {
                LoadSnapshot(snapshot);
            }
            finally
            {
                _loading = false;
            }

            logger.LogInformation(
                "Loaded {Jobs} jobs, {JobGroups} job groups",
                snapshot.Jobs.Length, snapshot.JobGroups.Length);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("No existing job data found in S3, starting fresh");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load jobs from S3, starting fresh");
        }

        await SaveAsync(cancellationToken);
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        jobs.OnAdded.Subscribe(_ => RequestSave());
        jobs.OnUpdated.Subscribe(_ => RequestSave());
        jobs.OnDeleted.Subscribe(_ => RequestSave());

        jobGroups.OnAdded.Subscribe(_ => RequestSave());
        jobGroups.OnUpdated.Subscribe(_ => RequestSave());
        jobGroups.OnDeleted.Subscribe(_ => RequestSave());

        _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

    private void RequestSave()
    {
        if (_loading) return;
        _dirty = true;
    }

    private async void OnTimerTick(object? state)
    {
        if (!_dirty) return;
        _dirty = false;

        await SaveAsync(CancellationToken.None);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (s3 is null)
        {
            logger.LogWarning("S3 not configured, skipping job save");
            return;
        }

        try
        {
            var snapshot = CreateSnapshot();

            var json = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                JobPersistenceJsonContext.Default.JobSnapshot);

            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = Key,
                InputStream = new MemoryStream(json),
                ContentType = "application/json",
            }, cancellationToken);

            logger.LogInformation(
                "Saved {Jobs} jobs, {JobGroups} job groups",
                snapshot.Jobs.Length, snapshot.JobGroups.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save jobs to S3");
        }
    }

    private JobSnapshot CreateSnapshot() =>
        new(jobs.List().ToArray(), jobGroups.List().ToArray());

    private void LoadSnapshot(JobSnapshot snapshot)
    {
        foreach (var group in snapshot.JobGroups)
            jobGroups.Set(group);

        // Restore jobs as-is. Scheduled/InProgress jobs are reconciled on the next JobRunner tick:
        // the executor calls TryGetJobStatusAsync(JobName(job.Id)) and either reattaches to the
        // existing K8s Job or submits a fresh one. K8s is the source of truth for in-flight state.
        foreach (var job in snapshot.Jobs)
            jobs.Set(job);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
