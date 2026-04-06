using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;

namespace Olve.Pipelines.Shared.Persistence;

public class ConfigurationPersistenceService(
    EntityStore<Pipeline> pipelines,
    EntityStore<ProductionStep> productionSteps,
    AttachmentStore<ProductionStep, StepConfiguration> productionConfigs,
    EntityStore<ProcessingStep> processingSteps,
    AttachmentStore<ProcessingStep, StepConfiguration> processingConfigs,
    StorageOptions storageOptions,
    ILogger<ConfigurationPersistenceService> logger,
    IAmazonS3? s3 = null) : IHostedLifecycleService
{
    private const string Key = "configuration.json";

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

            LoadSnapshot(snapshot);

            logger.LogInformation(
                "Loaded configuration: {Pipelines} pipelines, {ProductionSteps} production steps, {ProcessingSteps} processing steps",
                snapshot.Pipelines?.Length ?? 0, snapshot.ProductionSteps?.Length ?? 0, snapshot.ProcessingSteps?.Length ?? 0);
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

    public Task StoppingAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

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
                "Saved configuration: {Pipelines} pipelines, {ProductionSteps} production steps, {ProcessingSteps} processing steps",
                snapshot.Pipelines.Length, snapshot.ProductionSteps.Length, snapshot.ProcessingSteps.Length);
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
            }).ToArray());
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
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
