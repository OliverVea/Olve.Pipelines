using Olve.MinimalApi;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.Pipelines.Api;

public static class PipelineEndpoints
{
    public static void MapPipelineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines");

        group.MapPost("/", Result<Pipeline> (PipelineService service, string name) =>
        {
            var pipeline = service.Create(name);
            return Result.Success(pipeline);
        })
        .WithResultMapping<Pipeline>();

        group.MapGet("/{id}", Result<Pipeline> (PipelineService service, Id<Pipeline> id) =>
        {
            if (!service.TryGet(id, out var pipeline))
            {
                return Result.Failure<Pipeline>(new ResultProblem($"Pipeline '{id}' not found."));
            }

            return Result.Success(pipeline);
        })
        .WithResultMapping<Pipeline>()
        .AllowAnonymous();

        group.MapGet("/", Result<Pipeline[]> (PipelineService service) =>
        {
            var pipelines = service.List().ToArray();
            return Result.Success(pipelines);
        })
        .WithResultMapping<Pipeline[]>()
        .AllowAnonymous();

        group.MapDelete("/{id}", DeletionResult (PipelineService service, Id<Pipeline> id) =>
            service.Delete(id))
        .WithDeletionMapping();
    }
}
