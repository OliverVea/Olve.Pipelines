using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Sourcing.Api;

public static class SourceBundleEndpoints
{
    public static void MapSourceBundleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/source-bundles");

        group.MapGet("/", Result<SourceBundle[]> (
            PipelineService pipelines,
            SourceBundleService sourceBundles,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
                return Result.Failure<SourceBundle[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

            var bundles = sourceBundles.GetByPipelineId(pipelineIdTyped);
            return Result.Success(bundles.ToArray());
        })
        .WithResultMapping<SourceBundle[]>();

        group.MapGet("/{bundleId:guid}", Result<SourceBundle> (
            SourceBundleService sourceBundles,
            Guid bundleId) =>
        {
            var bundleIdTyped = new Id<SourceBundle>(new Id(bundleId));

            if (!sourceBundles.TryGet(bundleIdTyped, out var bundle))
                return Result.Failure<SourceBundle>(new ResultProblem($"Source bundle '{bundleId}' not found."));

            return Result.Success(bundle);
        })
        .WithResultMapping<SourceBundle>();
    }
}
