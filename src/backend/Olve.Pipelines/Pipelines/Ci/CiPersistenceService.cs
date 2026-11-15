using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>CI's persisted state (<c>ci-state.json</c>): the tracked pull requests, the index of CI runs and the builds of pull requests' own definitions.</summary>
public record CiStateSnapshot(PullRequest[] Pulls, CiRunKey[]? Runs = null, CiBuild[]? Builds = null);

/// <summary>A CI run in the index: its state lives at <c>ci/&lt;p&gt;/&lt;number&gt;/run.json</c>.</summary>
public record CiRunKey(Id<Pipeline> PipelineId, int Number);

/// <summary>One CI run and all its attempts (<c>ci/&lt;p&gt;/&lt;number&gt;/run.json</c>).</summary>
public record CiRunDocument(CiRun Run, CiAttempt[] Attempts);

[JsonSerializable(typeof(CiStateSnapshot))]
[JsonSerializable(typeof(CiRunDocument))]
internal partial class CiPersistenceJsonContext : JsonSerializerContext;

/// <summary>
/// Persists CI's state: <c>ci-state.json</c> (pull requests, the run index) and one <c>run.json</c> per CI run.
/// Loaded before startup work runs; saved within a second of a change, on shutdown, and at once through
/// <see cref="SavedAsync"/> (a CI attempt is saved before its Job exists). A snapshot that can't be read fails
/// startup rather than being overwritten with empty state. Singleton.
/// </summary>
public class CiPersistenceService(
    EntityStore<PullRequest> pulls,
    EntityStore<CiRun> runs,
    EntityStore<CiAttempt> attempts,
    EntityStore<CiBuild> builds,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<CiPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    internal const string Key = "ci-state.json";
    private const string ReadinessKey = "ci";

    internal TimeSpan SaveTimeout { get; init; } = TimeSpan.FromSeconds(15);

    private volatile bool _dirty;
    private volatile bool _loading;
    private volatile bool _loaded;
    private FlushTimer? _timer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConcurrentDictionary<Id<CiRun>, bool> _dirtyRuns = new();
    private readonly ConcurrentDictionary<Id<CiRun>, CiRunKey> _deletedRuns = new();
    private CoalescingFlush? _saves;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        pulls.OnAdded.Subscribe(_ => RequestSave());
        pulls.OnUpdated.Subscribe(_ => RequestSave());
        pulls.OnDeleted.Subscribe(_ => RequestSave());
        builds.OnAdded.Subscribe(_ => RequestSave());
        builds.OnUpdated.Subscribe(_ => RequestSave());
        builds.OnDeleted.Subscribe(_ => RequestSave());
        runs.OnAdded.Subscribe(e => RunChanged(e.Id));
        runs.OnUpdated.Subscribe(e => RunChanged(e.Id));
        runs.OnDeleted.Subscribe(e => RunDeleted(e.Entity));
        attempts.OnAdded.Subscribe(e => RunChanged(e.Entity.RunId));
        attempts.OnUpdated.Subscribe(e => RunChanged(e.After.RunId));
        attempts.OnDeleted.Subscribe(e => RunChanged(e.Entity.RunId));

        readiness.Register(ReadinessKey);

        if (storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Ephemeral storage mode: CI state will not be persisted");
            readiness.MarkReady(ReadinessKey);
            return;
        }

        if (store is null)
        {
            throw new InvalidOperationException(
                "Persistent storage mode requires storage configuration (Storage:Endpoint and credentials), " +
                "but none was provided. Set Storage:Mode=Ephemeral for in-memory-only operation.");
        }

        CiStateSnapshot? snapshot;
        var documents = new List<CiRunDocument>();
        try
        {
            snapshot = await ReadAsync(Key, CiPersistenceJsonContext.Default.CiStateSnapshot, cancellationToken);
            foreach (var key in snapshot?.Runs ?? [])
            {
                var document = await ReadAsync(StorageLayout.CiRunStateKey(key.PipelineId, key.Number), CiPersistenceJsonContext.Default.CiRunDocument, cancellationToken)
                               ?? throw new InvalidOperationException($"CI run #{key.Number} of pipeline '{key.PipelineId}' is in the index, but its run.json is missing.");
                documents.Add(document);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Could not load CI state; failing startup to avoid overwriting good state");
            throw;
        }

        _loading = true;
        try
        {
            foreach (var pull in snapshot?.Pulls ?? [])
                pulls.Set(pull);
            foreach (var build in snapshot?.Builds ?? [])
                builds.Set(build);
            foreach (var document in documents)
            {
                runs.Set(document.Run);
                foreach (var attempt in document.Attempts)
                    attempts.Set(attempt);
            }
        }
        finally
        {
            _loading = false;
        }

        _loaded = true;
        if (snapshot is null)
            await SaveAsync(cancellationToken);
        readiness.MarkReady(ReadinessKey);
        logger.LogInformation("Loaded CI state: {Pulls} pull requests, {Runs} CI runs", snapshot?.Pulls.Length ?? 0, documents.Count);
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _timer = new FlushTimer(SaveIfDirtyAsync, logger, "CI state");
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

    /// <summary>Waits until CI's changes so far are saved; fails with an <see cref="UnavailableProblem"/> when the save failed or took too long.</summary>
    public async Task<Result> SavedAsync(CancellationToken ct = default)
    {
        if (store is null || !_loaded)
            return Result.Success();

        var saves = LazyInitializer.EnsureInitialized(ref _saves, () => new CoalescingFlush(async () =>
        {
            _dirty = false;
            return await SaveAsync(CancellationToken.None);
        }));
        try
        {
            if (await saves.RequestAsync().WaitAsync(SaveTimeout, ct))
                return Result.Success();
        }
        catch (TimeoutException)
        {
        }

        return new UnavailableProblem("The change was made but could not be saved; it may be lost if the server restarts.");
    }

    private void RequestSave()
    {
        if (_loading || !_loaded) return;
        _dirty = true;
    }

    private void RunChanged(Id<CiRun> runId)
    {
        if (_loading || !_loaded) return;
        _dirtyRuns[runId] = true;
        _dirty = true;
    }

    private void RunDeleted(CiRun run)
    {
        if (_loading || !_loaded) return;
        _dirtyRuns.TryRemove(run.Id, out _);
        _deletedRuns[run.Id] = new CiRunKey(run.PipelineId, run.Number);
        _dirty = true;
    }

    internal async Task SaveIfDirtyAsync()
    {
        if (!_dirty) return;
        _dirty = false;
        await SaveAsync(CancellationToken.None);
    }

    /// <returns>True when everything was written.</returns>
    private async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (store is null || !_loaded)
            return true;

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var runId in _dirtyRuns.Keys)
            {
                _dirtyRuns.TryRemove(runId, out _);
                if (!runs.TryGet(runId, out var run))
                    continue;
                var document = new CiRunDocument(run, [.. attempts.List().Where(a => a.RunId == runId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Number)]);
                try
                {
                    await store.WriteAsync(StorageLayout.CiRunStateKey(run.PipelineId, run.Number),
                        JsonSerializer.SerializeToUtf8Bytes(document, CiPersistenceJsonContext.Default.CiRunDocument), cancellationToken);
                }
                catch
                {
                    _dirtyRuns[runId] = true;
                    throw;
                }
            }

            var snapshot = new CiStateSnapshot(
                [.. pulls.List().OrderBy(p => p.PipelineId.Value.Value).ThenBy(p => p.Number)],
                [.. runs.List().OrderBy(r => r.PipelineId.Value.Value).ThenBy(r => r.Number).Select(r => new CiRunKey(r.PipelineId, r.Number))],
                [.. builds.List().OrderBy(b => b.PipelineId.Value.Value).ThenBy(b => b.CommitSha, StringComparer.Ordinal)]);
            await store.WriteAsync(Key, JsonSerializer.SerializeToUtf8Bytes(snapshot, CiPersistenceJsonContext.Default.CiStateSnapshot), cancellationToken);

            // Only once the index no longer names them.
            foreach (var (runId, key) in _deletedRuns)
            {
                if (_deletedRuns.TryRemove(runId, out _))
                    await store.DeleteAsync(StorageLayout.CiRunStateKey(key.PipelineId, key.Number), cancellationToken);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save CI state to storage");
            _dirty = true;
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<T?> ReadAsync<T>(string key, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        var data = await store!.TryReadAsync(key, ct);
        return data is null ? default : JsonSerializer.Deserialize(data, type);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _timer?.Dispose();
        _saveGate.Dispose();
    }
}
