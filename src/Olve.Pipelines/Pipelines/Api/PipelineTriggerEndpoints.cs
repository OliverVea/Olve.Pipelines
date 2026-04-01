using Olve.MinimalApi;
using Olve.Pipelines.Building;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Processing;
using Olve.Pipelines.Sourcing;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Pipelines.Api;

public static class PipelineTriggerEndpoints
{
    public static void MapPipelineTriggerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/trigger");

        group.MapPost("/sourcing", async Task<Result<SourceBundle>> (
            PipelineService pipelines,
            JobRunnerService jobRunner,
            Id<Pipeline> pipelineId,
            CancellationToken ct) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<SourceBundle>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            return await jobRunner.RunSourcingAsync(pipelineId, ct);
        })
        .WithResultMapping<SourceBundle>();

        group.MapPost("/building", async Task<Result<ArtifactBundle>> (
            PipelineService pipelines,
            SourceBundleService sourceBundles,
            JobRunnerService jobRunner,
            Id<Pipeline> pipelineId,
            CancellationToken ct) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var latestSource = sourceBundles.GetLatest(pipelineId);
            if (latestSource is null)
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' has no source bundle. Run sourcing first."));
            }

            return await jobRunner.RunBuildingAsync(pipelineId, latestSource.Id, ct);
        })
        .WithResultMapping<ArtifactBundle>();

        group.MapPost("/processing/{processingStepId}", async Task<Result<ArtifactBundle>> (
            PipelineService pipelines,
            ProcessingStepService processingSteps,
            ArtifactBundleService artifactBundles,
            JobRunnerService jobRunner,
            Id<Pipeline> pipelineId,
            Id<ProcessingStep> processingStepId,
            CancellationToken ct) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            if (!processingSteps.TryGet(processingStepId, out _))
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Processing step '{processingStepId}' not found."));
            }

            var latestArtifact = artifactBundles.GetLatest(pipelineId);
            if (latestArtifact is null)
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' has no artifact bundle. Run building first."));
            }

            return await jobRunner.RunProcessingAsync(pipelineId, processingStepId, latestArtifact.Id, ct);
        })
        .WithResultMapping<ArtifactBundle>();
    }
}
