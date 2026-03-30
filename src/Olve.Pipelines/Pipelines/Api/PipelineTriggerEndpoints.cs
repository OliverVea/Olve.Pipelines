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
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/trigger");

        group.MapPost("/sourcing", async Task<Result<SourceBundle>> (
            PipelineService pipelines,
            JobRunnerService jobRunner,
            Guid pipelineId,
            CancellationToken ct) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<SourceBundle>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            return await jobRunner.RunSourcingAsync(pipelineIdTyped, ct);
        })
        .WithResultMapping<SourceBundle>();

        group.MapPost("/building", async Task<Result<ArtifactBundle>> (
            PipelineService pipelines,
            SourceBundleService sourceBundles,
            JobRunnerService jobRunner,
            Guid pipelineId,
            CancellationToken ct) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var latestSource = sourceBundles.GetLatest(pipelineIdTyped);
            if (latestSource is null)
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' has no source bundle. Run sourcing first."));
            }

            return await jobRunner.RunBuildingAsync(pipelineIdTyped, latestSource.Id, ct);
        })
        .WithResultMapping<ArtifactBundle>();

        group.MapPost("/processing/{processingStepId:guid}", async Task<Result<ArtifactBundle>> (
            PipelineService pipelines,
            ProcessingStepService processingSteps,
            ArtifactBundleService artifactBundles,
            JobRunnerService jobRunner,
            Guid pipelineId,
            Guid processingStepId,
            CancellationToken ct) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));
            var processingStepIdTyped = new Id<ProcessingStep>(new Id(processingStepId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            if (!processingSteps.TryGet(processingStepIdTyped, out _))
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Processing step '{processingStepId}' not found."));
            }

            var latestArtifact = artifactBundles.GetLatest(pipelineIdTyped);
            if (latestArtifact is null)
            {
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Pipeline '{pipelineId}' has no artifact bundle. Run building first."));
            }

            return await jobRunner.RunProcessingAsync(pipelineIdTyped, processingStepIdTyped, latestArtifact.Id, ct);
        })
        .WithResultMapping<ArtifactBundle>();
    }
}
