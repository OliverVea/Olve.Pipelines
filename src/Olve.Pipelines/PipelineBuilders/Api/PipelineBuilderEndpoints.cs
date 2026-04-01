using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineBuilders.Api;

public static class PipelineBuilderEndpoints
{
    public record CreatePipelineBuilderRequest(string Name);
    public record SetScriptBuilderRequest(string Script);

    public static void MapPipelineBuilderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/builders");

        group.MapPost("/", Result<PipelineBuilder> (
            PipelineService pipelines,
            PipelineBuilderService builders,
            Id<Pipeline> pipelineId,
            CreatePipelineBuilderRequest request) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<PipelineBuilder>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var builder = new PipelineBuilder(Id.New<PipelineBuilder>(), request.Name, pipelineId);
            builders.Set(builder);
            return Result.Success(builder);
        })
        .WithResultMapping<PipelineBuilder>();

        group.MapGet("/{builderId}", Result<PipelineBuilder> (
            PipelineBuilderService builders,
            Id<PipelineBuilder> builderId) =>
        {
            if (!builders.TryGet(builderId, out var builder))
            {
                return Result.Failure<PipelineBuilder>(new ResultProblem($"Builder '{builderId}' not found."));
            }

            return Result.Success(builder);
        })
        .WithResultMapping<PipelineBuilder>()
        .AllowAnonymous();

        group.MapGet("/", Result<PipelineBuilder[]> (
            PipelineService pipelines,
            PipelineBuilderService builders,
            Id<Pipeline> pipelineId) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<PipelineBuilder[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var pipelineBuilders = builders.GetByPipelineId(pipelineId).ToArray();
            return Result.Success(pipelineBuilders);
        })
        .WithResultMapping<PipelineBuilder[]>()
        .AllowAnonymous();

        group.MapDelete("/{builderId}", DeletionResult (
            PipelineBuilderService builders,
            Id<PipelineBuilder> builderId) =>
            builders.Delete(builderId))
        .WithDeletionMapping();

        // Script builder attachment
        group.MapPut("/{builderId}/script", Result<ScriptBuilder> (
            PipelineBuilderService builders,
            Id<PipelineBuilder> builderId,
            SetScriptBuilderRequest request) =>
        {
            if (!builders.TryGet(builderId, out _))
                return Result.Failure<ScriptBuilder>(new ResultProblem($"Builder '{builderId}' not found."));

            var script = new ScriptBuilder(request.Script);
            builders.SetScript(builderId, script);
            return Result.Success(script);
        })
        .WithResultMapping<ScriptBuilder>();

        group.MapGet("/{builderId}/script", Result<ScriptBuilder> (
            PipelineBuilderService builders,
            Id<PipelineBuilder> builderId) =>
        {
            if (!builders.TryGetScript(builderId, out var script))
                return Result.Failure<ScriptBuilder>(new ResultProblem($"Builder '{builderId}' has no script configuration."));

            return Result.Success(script);
        })
        .WithResultMapping<ScriptBuilder>()
        .AllowAnonymous();

        group.MapDelete("/{builderId}/script", (
            PipelineBuilderService builders,
            Id<PipelineBuilder> builderId) =>
        {
            if (!builders.RemoveScript(builderId))
                return Result.Failure(new ResultProblem($"Builder '{builderId}' has no script configuration."));

            return Result.Success();
        })
        .WithResultMapping();
    }
}
