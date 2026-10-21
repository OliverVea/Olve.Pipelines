using Olve.MinimalApi;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>Minimal read-only run endpoints (B3). The full runs/attempts/outputs API is B8.</summary>
public static class RunEndpoints
{
    public static void MapRunEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/runs");

        group.MapGet("/", Result<Run[]> (RunService service, Id<Pipeline> pipelineId)
                => service.ListByPipeline(pipelineId).ToArray())
            .WithResultMapping<Run[]>()
            .WithName("ListRuns")
            .AllowAnonymous();

        group.MapGet("/{number:int}", Result<Run> (RunService service, Id<Pipeline> pipelineId, int number)
                => service.TryGetByNumber(pipelineId, number, out var run)
                    ? run
                    : new ResultProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId))
            .WithResultMapping<Run>()
            .WithName("GetRun")
            .AllowAnonymous();

        group.MapGet("/{number:int}/attempts", Result<Attempt[]> (RunService runs, AttemptService attempts, Id<Pipeline> pipelineId, int number)
                => runs.TryGetByNumber(pipelineId, number, out var run)
                    ? attempts.ListByRun(run.Id).ToArray()
                    : new ResultProblem("Run #{0} not found in pipeline '{1}'.", number, pipelineId))
            .WithResultMapping<Attempt[]>()
            .WithName("ListRunAttempts")
            .AllowAnonymous();
    }
}
