using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Sourcing.Api;

public static class SourceBundleEndpoints
{
    public static void MapSourceBundleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/source-bundles");

        group.MapGet("/", Result<SourceBundle[]> (
            PipelineService pipelines,
            SourceBundleService sourceBundles,
            Id<Pipeline> pipelineId) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
                return Result.Failure<SourceBundle[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var bundles = sourceBundles.GetByPipelineId(pipelineId);
            return Result.Success(bundles.ToArray());
        })
        .WithResultMapping<SourceBundle[]>();

        group.MapGet("/{bundleId}", Result<SourceBundle> (
            SourceBundleService sourceBundles,
            Id<SourceBundle> bundleId) =>
        {
            if (!sourceBundles.TryGet(bundleId, out var bundle))
                return Result.Failure<SourceBundle>(new ResultProblem($"Source bundle '{bundleId}' not found."));

            return Result.Success(bundle);
        })
        .WithResultMapping<SourceBundle>();
    }
}
