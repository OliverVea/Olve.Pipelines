using System.Text.Json;
using System.Text.Json.Serialization;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>Persisted binding webhook registrations (the live hook ids GitHub assigned).</summary>
public record BindingHookSnapshot(BindingHookEntry[] Hooks);

public record BindingHookEntry(
    Id<PipelineConfigBinding> BindingId,
    Id<Pipeline> PipelineId,
    string Owner,
    string Repo,
    long HookId,
    string CredentialsSecret);

[JsonSerializable(typeof(BindingHookSnapshot))]
internal partial class BindingHookPersistenceJsonContext : JsonSerializerContext;

/// <summary>
/// Persists <see cref="BindingHookStateStore"/> to <c>binding-hooks.json</c> so registered hook ids
/// survive a restart (else a binding switched to poll/unbound after a restart could not have its repo
/// hook removed). Same write-gating as the other snapshot services.
/// </summary>
public class BindingHookPersistenceService(
    BindingHookStateStore hooks,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<BindingHookPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    private const string Key = "binding-hooks.json";
    private const string ReadinessKey = "binding-hooks";

    private volatile bool _dirty;
    private volatile bool _loading;
    private volatile bool _loaded;
    private Timer? _timer;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        readiness.Register(ReadinessKey);

        if (storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Ephemeral storage mode: binding hook state will not be persisted");
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
            logger.LogError(ex, "Failed to load binding hook state from storage; failing startup to avoid overwriting good state");
            throw;
        }

        if (data is null)
        {
            logger.LogInformation("No existing binding hook state found in storage, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        BindingHookSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(data, BindingHookPersistenceJsonContext.Default.BindingHookSnapshot);
        }
        catch (JsonException ex)
        {
            logger.LogCritical(ex, "Binding hook snapshot in storage is corrupt; failing startup (manual restore required)");
            throw;
        }

        if (snapshot is null)
        {
            logger.LogInformation("Binding hook snapshot was empty, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        _loading = true;
        try
        {
            foreach (var hook in snapshot.Hooks ?? [])
                hooks.Set(hook.BindingId, new BindingHookState(hook.PipelineId, hook.Owner, hook.Repo, hook.HookId, hook.CredentialsSecret));
        }
        finally
        {
            _loading = false;
        }

        _loaded = true;
        readiness.MarkReady(ReadinessKey);

        logger.LogInformation("Loaded binding hook state: {Hooks} hooks", snapshot.Hooks?.Length ?? 0);
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        hooks.OnSet.Subscribe(_ => RequestSave());
        hooks.OnRemoved.Subscribe(_ => RequestSave());

        _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

    private void RequestSave()
    {
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
        if (store is null || !_loaded)
            return;

        try
        {
            var entries = hooks.GetAll()
                .Select(kvp => new BindingHookEntry(
                    kvp.Key, kvp.Value.PipelineId, kvp.Value.Owner, kvp.Value.Repo, kvp.Value.HookId, kvp.Value.CredentialsSecret))
                .ToArray();

            var snapshot = new BindingHookSnapshot(entries);

            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, BindingHookPersistenceJsonContext.Default.BindingHookSnapshot);

            await store.WriteAsync(Key, json, cancellationToken);

            logger.LogInformation("Saved binding hook state: {Hooks} hooks", entries.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save binding hook state to storage");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
