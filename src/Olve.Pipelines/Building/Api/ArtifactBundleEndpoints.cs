using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Sourcing;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Building.Api;

public static class ArtifactBundleEndpoints
{
    public static void MapArtifactBundleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/artifact-bundles");

        group.MapGet("/", Result<ArtifactBundle[]> (
            PipelineService pipelines,
            ArtifactBundleService artifactBundles,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
                return Result.Failure<ArtifactBundle[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var bundles = artifactBundles.GetByPipelineId(pipelineIdTyped);
            return Result.Success(bundles.ToArray());
        })
        .WithResultMapping<ArtifactBundle[]>();

        group.MapGet("/{bundleId:guid}", Result<ArtifactBundle> (
            ArtifactBundleService artifactBundles,
            Guid bundleId) =>
        {
            var bundleIdTyped = new Id<ArtifactBundle>(new Id(bundleId));

            if (!artifactBundles.TryGet(bundleIdTyped, out var bundle))
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Artifact bundle '{bundleId}' not found."));

            return Result.Success(bundle);
        })
        .WithResultMapping<ArtifactBundle>();
    }
}
