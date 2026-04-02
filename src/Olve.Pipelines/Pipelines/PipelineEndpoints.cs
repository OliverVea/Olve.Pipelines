using Olve.MinimalApi;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines;

public static class PipelineEndpoints
{
    public static void MapPipelineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines");

        group.MapPost("/", Result<Pipeline> (PipelineService service, string name)
                => service.Create(name))
            .WithResultMapping<Pipeline>()
            .WithName("CreatePipeline");

        group.MapGet("/{id}", Result<Pipeline> (PipelineService service, Id<Pipeline> id)
                => service.TryGet(id, out var pipeline)
                    ? Result.Success(pipeline)
                    : Result.Failure<Pipeline>(new ResultProblem($"Pipeline '{id}' not found.")))
            .WithResultMapping<Pipeline>()
            .WithName("GetPipeline")
            .AllowAnonymous();

        group.MapGet("/", Result<Pipeline[]> (PipelineService service)
                => service.List().ToArray())
            .WithResultMapping<Pipeline[]>()
            .WithName("ListPipelines")
            .AllowAnonymous();

        group.MapDelete("/{id}", DeletionResult (PipelineService service, Id<Pipeline> id)
                => service.Delete(id))
            .WithDeletionMapping()
            .WithName("DeletePipeline");

        group.MapPost("/{id}/trigger/production", Result<Job> (
            PipelineService pipelines,
            ProductionStepService productionSteps,
            JobService jobs,
            Id<Pipeline> id) =>
            {
                if (!pipelines.TryGet(id, out _))
                    return Result.Failure<Job>(new ResultProblem($"Pipeline '{id}' not found."));

                if (!productionSteps.HasConfiguredSteps(id))
                    return Result.Failure<Job>(new ResultProblem($"Pipeline '{id}' has no configured production steps."));

                return jobs.CreateProductionJob(id);
            })
            .WithResultMapping<Job>()
            .WithName("TriggerProduction");
    }
}
