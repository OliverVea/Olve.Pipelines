using Olve.Pipelines.Configuration;
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
            .WithApiResultMapping<Pipeline>()
            .WithName("GetPipeline")
            .WithTags("Pipelines")
            .WithSummary("Get a pipeline")
            .RequireRead();

        group.MapGet("/", Result<Pipeline[]> (PipelineService service)
                => service.List().ToArray())
            .WithApiResultMapping<Pipeline[]>()
            .WithName("ListPipelines")
            .WithTags("Pipelines")
            .WithSummary("List pipelines")
            .RequireRead();

        group.MapGet("/summary", Result<PipelineSummary[]> (PipelineSummaryService service)
                => service.List().ToArray())
            .WithApiResultMapping<PipelineSummary[]>()
            .WithName("ListPipelineSummaries")
            .WithTags("Pipelines")
            .WithSummary("List pipeline cards")
            .WithDescription("One card per pipeline for the list page: id, name, description, repo and health.")
            .RequireRead();

        group.MapDelete("/{id}", DeletionResult (PipelineService service, Id<Pipeline> id)
                => service.Delete(id))
            .WithDeletionMapping()
            .WithName("DeletePipeline")
            .WithTags("Pipelines")
            .WithSummary("Delete a pipeline")
            .WithDescription("Deletes the pipeline with its runs, versions, binding and secrets.");
    }
}
