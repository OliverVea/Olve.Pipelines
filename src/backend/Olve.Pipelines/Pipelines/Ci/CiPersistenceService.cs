using System.Text.Json;
using System.Text.Json.Serialization;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>CI's persisted state (<c>ci-state.json</c>): the tracked pull requests.</summary>
public record CiStateSnapshot(PullRequest[] Pulls);

[JsonSerializable(typeof(CiStateSnapshot))]
internal partial class CiPersistenceJsonContext : JsonSerializerContext;

/// <summary>
/// Persists CI's state to <c>ci-state.json</c>: loaded before startup work runs, saved within a second of a change
/// and on shutdown. A snapshot that can't be read fails startup rather than being overwritten with empty state.
/// </summary>
public class CiPersistenceService(
    EntityStore<PullRequest> pulls,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<CiPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    internal const string Key = "ci-state.json";
    private const string ReadinessKey = "ci";

    private volatile bool _dirty;
    private volatile bool _loading;
    private volatile bool _loaded;
    private FlushTimer? _timer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        pulls.OnAdded.Subscribe(_ => RequestSave());
        pulls.OnUpdated.Subscribe(_ => RequestSave());
        pulls.OnDeleted.Subscribe(_ => RequestSave());

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

        byte[]? data;
        try
        {
            data = await store.TryReadAsync(Key, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load CI state from storage; failing startup to avoid overwriting good state");
            throw;
        }

        CiStateSnapshot? snapshot = null;
        if (data is not null)
        {
            try
            {
                snapshot = JsonSerializer.Deserialize(data, CiPersistenceJsonContext.Default.CiStateSnapshot);
            }
            catch (JsonException ex)
            {
                logger.LogCritical(ex, "CI state in storage is corrupt; failing startup (manual restore required)");
                throw;
            }
        }

        _loading = true;
        try
        {
            foreach (var pull in snapshot?.Pulls ?? [])
                pulls.Set(pull);
        }
        finally
        {
            _loading = false;
        }

        _loaded = true;
        if (snapshot is null)
            await SaveAsync(cancellationToken);
        readiness.MarkReady(ReadinessKey);
        logger.LogInformation("Loaded CI state: {Pulls} pull requests", snapshot?.Pulls?.Length ?? 0);
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _timer = new FlushTimer(SaveIfDirtyAsync, logger, "CI state");
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

    private void RequestSave()
    {
        if (_loading || !_loaded) return;
        _dirty = true;
    }

    internal Task SaveIfDirtyAsync()
    {
        if (!_dirty) return Task.CompletedTask;
        _dirty = false;
        return SaveAsync(CancellationToken.None);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (store is null || !_loaded)
            return;

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = new CiStateSnapshot([.. pulls.List().OrderBy(p => p.PipelineId.Value.Value).ThenBy(p => p.Number)]);
            await store.WriteAsync(Key, JsonSerializer.SerializeToUtf8Bytes(snapshot, CiPersistenceJsonContext.Default.CiStateSnapshot), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save CI state to storage");
            _dirty = true;
        }
        finally
        {
            _saveGate.Release();
        }
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
