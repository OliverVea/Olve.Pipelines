using Olve.MinimalApi;
using Olve.Pipelines.Building;
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

        group.MapPost("/sourcing", Result<SourceBundle> (
            PipelineService pipelines,
            SourceBundleService sourceBundles,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<SourceBundle>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var bundle = sourceBundles.Create(pipelineIdTyped);
            return Result.Success(bundle);
        })
        .WithResultMapping<SourceBundle>();

        group.MapPost("/building", Result<ArtifactBundle> (
            PipelineService pipelines,
            SourceBundleService sourceBundles,
            ArtifactBundleService artifactBundles,
            Guid pipelineId) =>
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

            var bundle = artifactBundles.Create(pipelineIdTyped, latestSource.Id);
            return Result.Success(bundle);
        })
        .WithResultMapping<ArtifactBundle>();

        group.MapPost("/processing/{processingStepId:guid}", Result<ArtifactBundle> (
            PipelineService pipelines,
            ProcessingStepService processingSteps,
            ArtifactBundleService artifactBundles,
            Guid pipelineId,
            Guid processingStepId) =>
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

            // Placeholder: in the future this will run the processing step and verifications.
            // For now, just return the latest artifact bundle.
            return Result.Success(latestArtifact);
        })
        .WithResultMapping<ArtifactBundle>();
    }
}
