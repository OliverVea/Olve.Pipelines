using System.Text.Json;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Jobs;

namespace Olve.Pipelines.Shared.Persistence;

public class JobPersistenceService(
    EntityStore<Job> jobs,
    EntityStore<JobGroup> jobGroups,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<JobPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    private const string Key = "jobs.json";
    private const string ReadinessKey = "jobs";

    private volatile bool _dirty;
    private volatile bool _loading;
    private volatile bool _loaded;
    private Timer? _timer;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        readiness.Register(ReadinessKey);

        if (storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Ephemeral storage mode: jobs will not be persisted");
            readiness.MarkReady(ReadinessKey);
            return;
        }

        if (store is null)
        {
            throw new InvalidOperationException(
                "Persistent storage mode requires storage configuration (Storage:Endpoint and credentials), " +
                "but none was provided. Set Storage:Mode=Ephemeral for in-memory-only operation.");
        }

        byte[]? data;
        try
        {
            data = await store.TryReadAsync(Key, cancellationToken);
        }
        catch (Exception ex)
        {
            // Transient/auth failure. Do NOT save — an unconditional save here would overwrite the
            // good snapshot with empty state. Fail startup so the pod crashloops and retries.
            logger.LogError(ex, "Failed to load jobs from storage; failing startup to avoid overwriting good state");
            throw;
        }

        if (data is null)
        {
            // First run: nothing is stored yet, so writing an empty baseline is safe.
            logger.LogInformation("No existing job data found in storage, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        JobSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(
                data,
                JobPersistenceJsonContext.Default.JobSnapshot);
        }
        catch (JsonException ex)
        {
            // Corrupt snapshot: terminal. Do NOT save. Fail loudly until a human restores it.
            logger.LogCritical(ex, "Job snapshot in storage is corrupt; failing startup (manual restore required)");
            throw;
        }

        if (snapshot is null)
        {
            logger.LogInformation("Job snapshot was empty, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        _loading = true;
        try
        {
            LoadSnapshot(snapshot);
        }
        finally
        {
            _loading = false;
        }

        // Successful load: state is already in memory, so there is nothing to write back.
        _loaded = true;
        readiness.MarkReady(ReadinessKey);

        logger.LogInformation(
            "Loaded {Jobs} jobs, {JobGroups} job groups",
            snapshot.Jobs.Length, snapshot.JobGroups.Length);
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
        // Never queue a save before a load has been confirmed — otherwise a save could write empty
        // state over a good snapshot.
        if (_loading || !_loaded) return;
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
        // Write-gate: never persist before a load has been confirmed (or with no store at all).
        if (store is null || !_loaded)
        {
            return;
        }

        try
        {
            var snapshot = CreateSnapshot();

            var json = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                JobPersistenceJsonContext.Default.JobSnapshot);

            await store.WriteAsync(Key, json, cancellationToken);

            logger.LogInformation(
                "Saved {Jobs} jobs, {JobGroups} job groups",
                snapshot.Jobs.Length, snapshot.JobGroups.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save jobs to storage");
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
