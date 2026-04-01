using Olve.MinimalApi;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public static class PipelineEndpoints
{
    public static void MapPipelineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines");

        group.MapPost("/", Result<Pipeline> (PipelineService service, string name)
                => service.Create(name))
            .WithResultMapping<Pipeline>();

        group.MapGet("/{id}", Result<Pipeline> (PipelineService service, Id<Pipeline> id)
                => service.TryGet(id, out var pipeline)
                    ? Result.Success(pipeline)
                    : Result.Failure<Pipeline>(new ResultProblem($"Pipeline '{id}' not found.")))
            .WithResultMapping<Pipeline>()
            .AllowAnonymous();

        group.MapGet("/", Result<Pipeline[]> (PipelineService service)
                => service.List().ToArray())
            .WithResultMapping<Pipeline[]>()
            .AllowAnonymous();

        group.MapDelete("/{id}", DeletionResult (PipelineService service, Id<Pipeline> id)
                => service.Delete(id))
            .WithDeletionMapping();
    }
}
