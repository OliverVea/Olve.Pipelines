using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Processing;

namespace Olve.Pipelines.Shared.Persistence;

public class ConfigurationPersistenceService(
    EntityStore<Pipeline> pipelines,
    EntityStore<PipelineSource> sources,
    EntityStore<PipelineBuilder> builders,
    EntityStore<ProcessingStep> processingSteps,
    EntityStore<Verification> verifications,
    AttachmentStore<PipelineSource, GitHubSource> githubSources,
    AttachmentStore<PipelineSource, HardcodedSource> hardcodedSources,
    AttachmentStore<PipelineBuilder, ScriptBuilder> scriptBuilders,
    AttachmentStore<ProcessingStep, ScriptProcessing> scriptProcessing,
    AttachmentStore<Verification, ScriptVerification> scriptVerifications,
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
                "Loaded configuration: {Pipelines} pipelines, {Sources} sources, {Builders} builders, {Steps} processing steps, {Verifications} verifications",
                snapshot.Pipelines.Length, snapshot.Sources.Length, snapshot.Builders.Length,
                snapshot.ProcessingSteps.Length, snapshot.Verifications.Length);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("No existing configuration found in S3, starting fresh");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load configuration from S3, starting fresh");
        }
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
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
                "Saved configuration: {Pipelines} pipelines, {Sources} sources, {Builders} builders, {Steps} processing steps, {Verifications} verifications",
                snapshot.Pipelines.Length, snapshot.Sources.Length, snapshot.Builders.Length,
                snapshot.ProcessingSteps.Length, snapshot.Verifications.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save configuration to S3");
        }
    }

    // TODO: Add periodic background save for crash resilience

    private ConfigurationSnapshot CreateSnapshot()
    {
        var ghAll = githubSources.GetAll();
        var hcAll = hardcodedSources.GetAll();
        var sbAll = scriptBuilders.GetAll();
        var spAll = scriptProcessing.GetAll();
        var svAll = scriptVerifications.GetAll();

        return new ConfigurationSnapshot(
            Pipelines: pipelines.List().Select(p => new PipelineData(p.Id, p.Name)).ToArray(),
            Sources: sources.List().Select(s =>
            {
                ghAll.TryGetValue(s.Id, out var gh);
                hcAll.TryGetValue(s.Id, out var hc);
                return new SourceData(
                    s.Id, s.Name, s.PipelineId, s.Type.ToString(),
                    gh is not null ? new GitHubSourceData(gh.Owner, gh.Repository, gh.Branch) : null,
                    hc is not null ? new HardcodedSourceData(hc.Files) : null);
            }).ToArray(),
            Builders: builders.List().Select(b =>
            {
                sbAll.TryGetValue(b.Id, out var script);
                return new BuilderData(
                    b.Id, b.Name, b.PipelineId, b.Type.ToString(),
                    script is not null ? new ScriptData(script.Script) : null);
            }).ToArray(),
            ProcessingSteps: processingSteps.List().Select(s =>
            {
                spAll.TryGetValue(s.Id, out var script);
                return new ProcessingStepData(
                    s.Id, s.Name, s.PipelineId, s.Type.ToString(),
                    script is not null ? new ScriptData(script.Script) : null);
            }).ToArray(),
            Verifications: verifications.List().Select(v =>
            {
                svAll.TryGetValue(v.Id, out var script);
                return new VerificationData(
                    v.Id, v.Name, v.ProcessingStepId, v.Type.ToString(),
                    script is not null ? new ScriptData(script.Script) : null);
            }).ToArray());
    }

    private void LoadSnapshot(ConfigurationSnapshot snapshot)
    {
        foreach (var p in snapshot.Pipelines)
            pipelines.Set(new Pipeline(p.Id, p.Name));

        foreach (var s in snapshot.Sources)
        {
            Enum.TryParse<PipelineSourceType>(s.Type, out var type);
            sources.Set(new PipelineSource(s.Id, s.Name, s.PipelineId, type));

            if (s.GitHub is not null)
                githubSources.Set(s.Id, new GitHubSource(s.GitHub.Owner, s.GitHub.Repository, s.GitHub.Branch));
            if (s.Hardcoded is not null)
                hardcodedSources.Set(s.Id, new HardcodedSource(s.Hardcoded.Files));
        }

        foreach (var b in snapshot.Builders)
        {
            Enum.TryParse<PipelineBuilderType>(b.Type, out var type);
            builders.Set(new PipelineBuilder(b.Id, b.Name, b.PipelineId, type));

            if (b.Script is not null)
                scriptBuilders.Set(b.Id, new ScriptBuilder(b.Script.Script));
        }

        foreach (var s in snapshot.ProcessingSteps)
        {
            Enum.TryParse<ProcessingStepType>(s.Type, out var type);
            processingSteps.Set(new ProcessingStep(s.Id, s.Name, s.PipelineId, type));

            if (s.Script is not null)
                scriptProcessing.Set(s.Id, new ScriptProcessing(s.Script.Script));
        }

        foreach (var v in snapshot.Verifications)
        {
            Enum.TryParse<VerificationType>(v.Type, out var type);
            verifications.Set(new Verification(v.Id, v.Name, v.ProcessingStepId, type));

            if (v.Script is not null)
                scriptVerifications.Set(v.Id, new ScriptVerification(v.Script.Script));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
