using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
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
    ILogger<ConfigurationPersistenceService> logger,
    IAmazonS3? s3 = null) : IHostedLifecycleService, IDisposable
{
    private const string Key = "configuration.json";

    private volatile bool _dirty;
    private volatile bool _loading;
    private Timer? _timer;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (s3 is null)
        {
            logger.LogWarning("S3 not configured, skipping configuration load");
            return;
        }

        try
        {
            var response = await s3.GetObjectAsync(storageOptions.Bucket, Key, cancellationToken);
            await using var stream = response.ResponseStream;

            var snapshot = await JsonSerializer.DeserializeAsync(
                stream,
                ConfigurationPersistenceJsonContext.Default.ConfigurationSnapshot,
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
                "Loaded configuration: {Pipelines} pipelines, {ProductionSteps} production steps, {ProcessingSteps} processing steps, {Triggers} triggers",
                snapshot.Pipelines?.Length ?? 0, snapshot.ProductionSteps?.Length ?? 0, snapshot.ProcessingSteps?.Length ?? 0, snapshot.Triggers?.Length ?? 0);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("No existing configuration found in S3, starting fresh");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load configuration from S3, starting fresh");
        }

        await SaveAsync(cancellationToken);
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
            logger.LogWarning("S3 not configured, skipping configuration save");
            return;
        }

        try
        {
            var snapshot = CreateSnapshot();

            var json = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                ConfigurationPersistenceJsonContext.Default.ConfigurationSnapshot);

            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storageOptions.Bucket,
                Key = Key,
                InputStream = new MemoryStream(json),
                ContentType = "application/json",
            }, cancellationToken);

            logger.LogInformation(
                "Saved configuration: {Pipelines} pipelines, {ProductionSteps} production steps, {ProcessingSteps} processing steps, {Triggers} triggers",
                snapshot.Pipelines.Length, snapshot.ProductionSteps.Length, snapshot.ProcessingSteps.Length, snapshot.Triggers?.Length ?? 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save configuration to S3");
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
            Bindings: bindings.List().Select(b => new PipelineConfigBindingData(b.Id, b.PipelineId, b.CreatedAt)).ToArray());
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
            bindings.Set(new PipelineConfigBinding(b.Id, b.PipelineId, b.CreatedAt));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
