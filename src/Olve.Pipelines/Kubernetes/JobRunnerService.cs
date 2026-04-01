using Olve.Pipelines.Building;
using Olve.Pipelines.PipelineBuilders;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.PipelineSources;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Sourcing;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Kubernetes;

public class JobRunnerService(
    KubernetesClient? kubernetesClient,
    string kubernetesNamespace,
    string defaultImage,
    JobTracker jobTracker,
    PipelineSourceService pipelineSources,
    PipelineBuilderService pipelineBuilders,
    ProcessingStepService processingSteps,
    SourceBundleService sourceBundles,
    ArtifactBundleService artifactBundles,
    ILogger<JobRunnerService> _logger)
{
    private KubernetesClient RequireKubernetesClient()
        => kubernetesClient ?? throw new InvalidOperationException("Kubernetes is not configured. Set Kubernetes:OpenBaoUrl and Storage credentials.");

    public async Task<Result<SourceBundle>> RunSourcingAsync(Id<Pipeline> pipelineId, CancellationToken ct = default)
    {
        var bundle = sourceBundles.Create(pipelineId, SourceBundleStatus.Pending);

        var sources = pipelineSources.GetByPipelineId(pipelineId);
        var script = BuildSourcingScript(sources);

        var spec = new KubernetesJobSpec(
            Name: $"olve-sourcing-{bundle.Id.Value.Value:N}",
            Image: "alpine/git:latest",
            Script: script,
            SecretName: $"olve-pipeline-{pipelineId.Value.Value:N}",
            OutputBundleS3Key: $"bundles/source/{bundle.Id.Value.Value}");

        _logger.LogInformation("Created sourcing Job {JobName} for pipeline {PipelineId}", spec.Name, pipelineId);
        await RequireKubernetesClient().CreateJobAsync(kubernetesNamespace, spec, ct);

        jobTracker.Track(new JobRecord(
            spec.Name, pipelineId, JobRecordPhase.Sourcing,
            SourceBundleId: bundle.Id, ArtifactBundleId: null,
            CreatedAt: DateTimeOffset.UtcNow));

        return Result.Success(bundle);
    }

    public async Task<Result<ArtifactBundle>> RunBuildingAsync(Id<Pipeline> pipelineId, Id<SourceBundle> sourceBundleId, CancellationToken ct = default)
    {
        var bundle = artifactBundles.Create(pipelineId, sourceBundleId, ArtifactBundleStatus.Pending);

        var builders = pipelineBuilders.GetByPipelineId(pipelineId);
        var script = BuildBuildingScript(builders);

        var spec = new KubernetesJobSpec(
            Name: $"olve-building-{bundle.Id.Value.Value:N}",
            Image: defaultImage,
            Script: script,
            SecretName: $"olve-pipeline-{pipelineId.Value.Value:N}",
            InputBundleS3Key: $"bundles/source/{sourceBundleId.Value.Value}",
            OutputBundleS3Key: $"bundles/artifact/{bundle.Id.Value.Value}");

        await RequireKubernetesClient().CreateJobAsync(kubernetesNamespace, spec, ct);

        jobTracker.Track(new JobRecord(
            spec.Name, pipelineId, JobRecordPhase.Building,
            SourceBundleId: sourceBundleId, ArtifactBundleId: bundle.Id,
            CreatedAt: DateTimeOffset.UtcNow));

        return Result.Success(bundle);
    }

    public async Task<Result<ArtifactBundle>> RunProcessingAsync(
        Id<Pipeline> pipelineId,
        Id<ProcessingStep> processingStepId,
        Id<ArtifactBundle> artifactBundleId,
        CancellationToken ct = default)
    {
        if (!processingSteps.TryGet(processingStepId, out _))
        {
            return Result.Failure<ArtifactBundle>(new ResultProblem($"Processing step '{processingStepId}' not found."));
        }

        if (!artifactBundles.TryGet(artifactBundleId, out var artifactBundle))
        {
            return Result.Failure<ArtifactBundle>(new ResultProblem($"Artifact bundle '{artifactBundleId}' not found."));
        }

        var script = BuildProcessingScript(processingStepId);

        var spec = new KubernetesJobSpec(
            Name: $"olve-processing-{processingStepId.Value.Value:N}",
            Image: defaultImage,
            Script: script,
            SecretName: $"olve-pipeline-{pipelineId.Value.Value:N}",
            InputBundleS3Key: $"bundles/artifact/{artifactBundleId.Value.Value}");

        await RequireKubernetesClient().CreateJobAsync(kubernetesNamespace, spec, ct);

        jobTracker.Track(new JobRecord(
            spec.Name, pipelineId, JobRecordPhase.Processing,
            SourceBundleId: null, ArtifactBundleId: artifactBundleId,
            CreatedAt: DateTimeOffset.UtcNow));

        return Result.Success(artifactBundle);
    }

    private string BuildSourcingScript(IReadOnlyList<PipelineSource> sources)
    {
        var parts = new List<string> { "set -e", "mkdir -p /workspace" };

        foreach (var source in sources)
        {
            if (pipelineSources.TryGetGitHub(source.Id, out var github))
            {
                parts.Add($"git clone --branch {github.Branch} --depth 1 https://github.com/{github.Owner}/{github.Repository}.git /workspace/{source.Name}");
            }
        }

        return string.Join("\n", parts);
    }

    private string BuildBuildingScript(IReadOnlyList<PipelineBuilder> builders)
    {
        var parts = new List<string> { "set -e", "mkdir -p /output" };

        foreach (var builder in builders)
        {
            if (pipelineBuilders.TryGetScript(builder.Id, out var script))
            {
                parts.Add($"# Builder: {builder.Name}");
                parts.Add($"mkdir -p /output/{builder.Name}");
                parts.Add(script.Script);
            }
        }

        return string.Join("\n", parts);
    }

    private string BuildProcessingScript(Id<ProcessingStep> stepId)
    {
        if (processingSteps.TryGetScript(stepId, out var script))
        {
            return $"set -e\n{script.Script}";
        }

        return "echo 'No script configured for processing step'";
    }
}
