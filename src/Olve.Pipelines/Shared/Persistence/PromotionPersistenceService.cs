using System.Text.Json;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Processing;

namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Persists the per-step promotion gate (the blocked set) so a braked step stays braked across a
/// pod restart — otherwise a restart would silently re-enable promotion and could auto-deploy.
///
/// Kept separate from <see cref="ConfigurationPersistenceService"/> on purpose: promotion is
/// operational state, not git-owned config, so it lives in its own snapshot. Mirrors the same
/// write-gating safety (never persist before a load is confirmed, never overwrite good state with
/// empty on a transient read failure).
/// </summary>
public class PromotionPersistenceService(
    AttachmentStore<ProcessingStep, ProcessingStepPromotion> promotions,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<PromotionPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    private const string Key = "promotion-state.json";
    private const string ReadinessKey = "promotion";

    private volatile bool _dirty;
    private volatile bool _loading;
    private volatile bool _loaded;
    private Timer? _timer;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        readiness.Register(ReadinessKey);

        if (storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Ephemeral storage mode: promotion state will not be persisted");
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
            logger.LogError(ex, "Failed to load promotion state from storage; failing startup to avoid overwriting good state");
            throw;
        }

        if (data is null)
        {
            // First run: nothing stored yet, so writing an empty baseline is safe.
            logger.LogInformation("No existing promotion state found in storage, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        PromotionSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(data, PromotionPersistenceJsonContext.Default.PromotionSnapshot);
        }
        catch (JsonException ex)
        {
            // Corrupt snapshot: terminal. Do NOT save. Fail loudly until a human restores it.
            logger.LogCritical(ex, "Promotion snapshot in storage is corrupt; failing startup (manual restore required)");
            throw;
        }

        if (snapshot is null)
        {
            logger.LogInformation("Promotion snapshot was empty, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        _loading = true;
        try
        {
            foreach (var blocked in snapshot.BlockedSteps ?? [])
                promotions.Set(blocked.ProcessingStepId, new ProcessingStepPromotion(true));
        }
        finally
        {
            _loading = false;
        }

        _loaded = true;
        readiness.MarkReady(ReadinessKey);

        logger.LogInformation("Loaded promotion state: {BlockedSteps} blocked steps", snapshot.BlockedSteps?.Length ?? 0);
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        promotions.OnSet.Subscribe(_ => RequestSave());
        promotions.OnRemoved.Subscribe(_ => RequestSave());

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
            return;

        try
        {
            var blocked = promotions.GetAll()
                .Where(kvp => kvp.Value.Blocked)
                .Select(kvp => new ProcessingStepPromotionData(kvp.Key))
                .ToArray();

            var snapshot = new PromotionSnapshot(blocked);

            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, PromotionPersistenceJsonContext.Default.PromotionSnapshot);

            await store.WriteAsync(Key, json, cancellationToken);

            logger.LogInformation("Saved promotion state: {BlockedSteps} blocked steps", blocked.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save promotion state to storage");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
