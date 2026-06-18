using System.Text.Json;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.GitHub;

/// <summary>
/// Persists <see cref="GitHubHookStateStore"/> to <c>github-hooks.json</c> so registered hook ids
/// survive a restart — without them a trigger deleted after a restart could not have its repo hook
/// removed. Mirrors the write-gating of the other snapshot services: never persist before a load is
/// confirmed, never overwrite good state with empty on a transient read failure.
/// </summary>
public class GitHubHookPersistenceService(
    GitHubHookStateStore hooks,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<GitHubHookPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    private const string Key = "github-hooks.json";
    private const string ReadinessKey = "github-hooks";

    private volatile bool _dirty;
    private volatile bool _loading;
    private volatile bool _loaded;
    private Timer? _timer;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        readiness.Register(ReadinessKey);

        if (storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Ephemeral storage mode: GitHub hook state will not be persisted");
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
            logger.LogError(ex, "Failed to load GitHub hook state from storage; failing startup to avoid overwriting good state");
            throw;
        }

        if (data is null)
        {
            logger.LogInformation("No existing GitHub hook state found in storage, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        GitHubHookSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(data, GitHubHookPersistenceJsonContext.Default.GitHubHookSnapshot);
        }
        catch (JsonException ex)
        {
            logger.LogCritical(ex, "GitHub hook snapshot in storage is corrupt; failing startup (manual restore required)");
            throw;
        }

        if (snapshot is null)
        {
            logger.LogInformation("GitHub hook snapshot was empty, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        _loading = true;
        try
        {
            foreach (var hook in snapshot.Hooks ?? [])
                hooks.Set(hook.TriggerId, new GitHubHookState(hook.PipelineId, hook.Owner, hook.Repo, hook.HookId, hook.TokenSecretName));
        }
        finally
        {
            _loading = false;
        }

        _loaded = true;
        readiness.MarkReady(ReadinessKey);

        logger.LogInformation("Loaded GitHub hook state: {Hooks} hooks", snapshot.Hooks?.Length ?? 0);
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
                .Select(kvp => new GitHubHookEntry(
                    kvp.Key, kvp.Value.PipelineId, kvp.Value.Owner, kvp.Value.Repo, kvp.Value.HookId, kvp.Value.TokenSecretName))
                .ToArray();

            var snapshot = new GitHubHookSnapshot(entries);

            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, GitHubHookPersistenceJsonContext.Default.GitHubHookSnapshot);

            await store.WriteAsync(Key, json, cancellationToken);

            logger.LogInformation("Saved GitHub hook state: {Hooks} hooks", entries.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save GitHub hook state to storage");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
