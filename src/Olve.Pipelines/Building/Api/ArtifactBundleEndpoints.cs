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
        var group = app.MapGroup("/api/pipelines/{pipelineId}/artifact-bundles");

        group.MapGet("/", Result<ArtifactBundle[]> (
            PipelineService pipelines,
            ArtifactBundleService artifactBundles,
            Id<Pipeline> pipelineId) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
                return Result.Failure<ArtifactBundle[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var bundles = artifactBundles.GetByPipelineId(pipelineId);
            return Result.Success(bundles.ToArray());
        })
        .WithResultMapping<ArtifactBundle[]>();

        group.MapGet("/{bundleId}", Result<ArtifactBundle> (
            ArtifactBundleService artifactBundles,
            Id<ArtifactBundle> bundleId) =>
        {
            if (!artifactBundles.TryGet(bundleId, out var bundle))
                return Result.Failure<ArtifactBundle>(new ResultProblem($"Artifact bundle '{bundleId}' not found."));

            return Result.Success(bundle);
        })
        .WithResultMapping<ArtifactBundle>();
    }
}
