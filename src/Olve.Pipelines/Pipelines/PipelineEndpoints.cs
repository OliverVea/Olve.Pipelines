using Olve.MinimalApi;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public static class PipelineEndpoints
{
    public static void MapPipelineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines");

        // Pipelines are created already bound to a repo via POST /api/pipelines/with-repo
        // (PipelineBindingEndpoints). GitOps is the only writer of pipeline shape, so there is no
        // bare, unbound create — a pipeline without a binding would be a permanently empty shell.

        group.MapGet("/{id}", Result<Pipeline> (PipelineService service, Id<Pipeline> id)
                => service.TryGet(id, out var pipeline)
                    ? Result.Success(pipeline)
                    : Result.Failure<Pipeline>(new NotFoundProblem("Pipeline '{0}' not found.", id)))
            .WithResultMapping<Pipeline>()
            .WithName("GetPipeline")
            .AllowAnonymous();

        group.MapGet("/", Result<Pipeline[]> (PipelineService service)
                => service.List().ToArray())
            .WithResultMapping<Pipeline[]>()
            .WithName("ListPipelines")
            .AllowAnonymous();

        group.MapGet("/summary", Result<PipelineSummary[]> (PipelineSummaryService service)
                => service.List().ToArray())
            .WithResultMapping<PipelineSummary[]>()
            .WithName("ListPipelineSummaries")
            .AllowAnonymous();

        group.MapDelete("/{id}", DeletionResult (PipelineService service, Id<Pipeline> id)
                => service.Delete(id))
            .WithDeletionMapping()
            .WithName("DeletePipeline");
    }
}
