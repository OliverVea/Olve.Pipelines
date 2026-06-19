using System.Text.Json;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.Shared.Persistence;

public class ConfigurationPersistenceService(
    EntityStore<Pipeline> pipelines,
    EntityStore<ProductionStep> productionSteps,
    AttachmentStore<ProductionStep, StepConfiguration> productionConfigs,
    EntityStore<ProcessingStep> processingSteps,
    AttachmentStore<ProcessingStep, StepConfiguration> processingConfigs,
    EntityStore<Trigger> triggers,
    EntityStore<PipelineConfigBinding> bindings,
    StorageOptions storageOptions,
    IPersistenceReadiness readiness,
    ILogger<ConfigurationPersistenceService> logger,
    ISnapshotStore? store = null) : IHostedLifecycleService, IDisposable
{
    private const string Key = "configuration.json";
    private const string ReadinessKey = "configuration";

    private volatile bool _dirty;
    private volatile bool _loading;
    private volatile bool _loaded;
    private Timer? _timer;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        readiness.Register(ReadinessKey);

        if (storageOptions.Mode == StorageMode.Ephemeral)
        {
            logger.LogInformation("Ephemeral storage mode: configuration will not be persisted");
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
            logger.LogError(ex, "Failed to load configuration from storage; failing startup to avoid overwriting good state");
            throw;
        }

        if (data is null)
        {
            // First run: nothing is stored yet, so writing an empty baseline is safe.
            logger.LogInformation("No existing configuration found in storage, starting fresh");
            _loaded = true;
            await SaveAsync(cancellationToken);
            readiness.MarkReady(ReadinessKey);
            return;
        }

        ConfigurationSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(
                data,
                ConfigurationPersistenceJsonContext.Default.ConfigurationSnapshot);
        }
        catch (JsonException ex)
        {
            // Corrupt snapshot: terminal. Do NOT save. Fail loudly until a human restores it.
            logger.LogCritical(ex, "Configuration snapshot in storage is corrupt; failing startup (manual restore required)");
            throw;
        }

        if (snapshot is null)
        {
            logger.LogInformation("Configuration snapshot was empty, starting fresh");
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
            "Loaded configuration: {Pipelines} pipelines, {ProductionSteps} production steps, {ProcessingSteps} processing steps, {Triggers} triggers",
            snapshot.Pipelines?.Length ?? 0, snapshot.ProductionSteps?.Length ?? 0, snapshot.ProcessingSteps?.Length ?? 0, snapshot.Triggers?.Length ?? 0);
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        pipelines.OnAdded.Subscribe(_ => RequestSave());
        pipelines.OnUpdated.Subscribe(_ => RequestSave());
        pipelines.OnDeleted.Subscribe(_ => RequestSave());

        productionSteps.OnAdded.Subscribe(_ => RequestSave());
        productionSteps.OnUpdated.Subscribe(_ => RequestSave());
        productionSteps.OnDeleted.Subscribe(_ => RequestSave());
        productionConfigs.OnSet.Subscribe(_ => RequestSave());
        productionConfigs.OnRemoved.Subscribe(_ => RequestSave());

        processingSteps.OnAdded.Subscribe(_ => RequestSave());
        processingSteps.OnUpdated.Subscribe(_ => RequestSave());
        processingSteps.OnDeleted.Subscribe(_ => RequestSave());
        processingConfigs.OnSet.Subscribe(_ => RequestSave());
        processingConfigs.OnRemoved.Subscribe(_ => RequestSave());

        triggers.OnAdded.Subscribe(_ => RequestSave());
        triggers.OnUpdated.Subscribe(_ => RequestSave());
        triggers.OnDeleted.Subscribe(_ => RequestSave());

        bindings.OnAdded.Subscribe(_ => RequestSave());
        bindings.OnUpdated.Subscribe(_ => RequestSave());
        bindings.OnDeleted.Subscribe(_ => RequestSave());

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
                ConfigurationPersistenceJsonContext.Default.ConfigurationSnapshot);

            await store.WriteAsync(Key, json, cancellationToken);

            logger.LogInformation(
                "Saved configuration: {Pipelines} pipelines, {ProductionSteps} production steps, {ProcessingSteps} processing steps, {Triggers} triggers",
                snapshot.Pipelines.Length, snapshot.ProductionSteps.Length, snapshot.ProcessingSteps.Length, snapshot.Triggers?.Length ?? 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save configuration to storage");
        }
    }

    private ConfigurationSnapshot CreateSnapshot()
    {
        var prodConfigs = productionConfigs.GetAll();
        var procConfigs = processingConfigs.GetAll();

        return new ConfigurationSnapshot(
            Pipelines: pipelines.List().Select(p => new PipelineData(p.Id, p.Name)).ToArray(),
            ProductionSteps: productionSteps.List().Select(s =>
            {
                prodConfigs.TryGetValue(s.Id, out var config);
                return new ProductionStepData(
                    s.Id, s.Name, s.PipelineId,
                    config is not null ? new StepConfigurationData(config.Image, config.Script, config.EnvironmentVariables) : null);
            }).ToArray(),
            ProcessingSteps: processingSteps.List().Select(s =>
            {
                procConfigs.TryGetValue(s.Id, out var config);
                return new ProcessingStepData(
                    s.Id, s.Name, s.PipelineId, s.Order,
                    config is not null ? new StepConfigurationData(config.Image, config.Script, config.EnvironmentVariables) : null);
            }).ToArray(),
            Triggers: triggers.List().Select(t => new TriggerData(t.Id, t.PipelineId, t.Name, t.Target, t.Secret, t.CreatedAt)).ToArray(),
            Bindings: bindings.List().Select(b => new PipelineConfigBindingData(
                b.Id, b.PipelineId, b.Repo, b.Branch, b.Path, b.CredentialsSecret, b.LastDeployedSha, b.LastSyncedSha, b.Status, b.CreatedAt,
                b.DeployTrigger, b.WebhookSecret)).ToArray());
    }

    private void LoadSnapshot(ConfigurationSnapshot snapshot)
    {
        foreach (var p in snapshot.Pipelines ?? [])
            pipelines.Set(new Pipeline(p.Id, p.Name));

        foreach (var s in snapshot.ProductionSteps ?? [])
        {
            productionSteps.Set(new ProductionStep(s.Id, s.Name, s.PipelineId));

            if (s.Configuration is not null)
                productionConfigs.Set(s.Id, new StepConfiguration(s.Configuration.Image, s.Configuration.Script, s.Configuration.EnvironmentVariables));
        }

        foreach (var s in snapshot.ProcessingSteps ?? [])
        {
            processingSteps.Set(new ProcessingStep(s.Id, s.Name, s.PipelineId, s.Order));

            if (s.Configuration is not null)
                processingConfigs.Set(s.Id, new StepConfiguration(s.Configuration.Image, s.Configuration.Script, s.Configuration.EnvironmentVariables));
        }

        foreach (var t in snapshot.Triggers ?? [])
            triggers.Set(new Trigger(t.Id, t.PipelineId, t.Name, t.Target, t.Secret, t.CreatedAt));

        foreach (var b in snapshot.Bindings ?? [])
            bindings.Set(new PipelineConfigBinding(
                b.Id, b.PipelineId, b.Repo, b.Branch, b.Path, b.CredentialsSecret, b.LastDeployedSha, b.LastSyncedSha,
                b.Status ?? ReconcileStatus.NeverRun, b.CreatedAt,
                // Pre-mode snapshots default to Poll — preserve their existing behavior, don't auto-adopt webhooks.
                b.DeployTrigger ?? BindingDeployTrigger.Poll, b.WebhookSecret));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
