using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineBuilders.Api;

public static class PipelineBuilderEndpoints
{
    public record CreatePipelineBuilderRequest(string Name);
    public record SetScriptBuilderRequest(string Script);

    public static void MapPipelineBuilderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/builders");

        group.MapPost("/", Result<PipelineBuilder> (
            PipelineService pipelines,
            PipelineBuilderService builders,
            Guid pipelineId,
            CreatePipelineBuilderRequest request) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineBuilder>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var builder = new PipelineBuilder(Id.New<PipelineBuilder>(), request.Name, pipelineIdTyped);
            builders.Set(builder);
            return Result.Success(builder);
        })
        .WithResultMapping<PipelineBuilder>();

        group.MapGet("/{builderId:guid}", Result<PipelineBuilder> (
            PipelineBuilderService builders,
            Guid builderId) =>
        {
            var builderIdTyped = new Id<PipelineBuilder>(new Id(builderId));

            if (!builders.TryGet(builderIdTyped, out var builder))
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
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineBuilder[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var pipelineBuilders = builders.GetByPipelineId(pipelineIdTyped).ToArray();
            return Result.Success(pipelineBuilders);
        })
        .WithResultMapping<PipelineBuilder[]>()
        .AllowAnonymous();

        group.MapDelete("/{builderId:guid}", (
            PipelineBuilderService builders,
            Guid builderId) =>
        {
            var builderIdTyped = new Id<PipelineBuilder>(new Id(builderId));

            if (!builders.Delete(builderIdTyped))
            {
                return Result.Failure(new ResultProblem($"Builder '{builderId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();

        // Script builder attachment
        group.MapPut("/{builderId:guid}/script", Result<ScriptBuilder> (
            PipelineBuilderService builders,
            Guid builderId,
            SetScriptBuilderRequest request) =>
        {
            var id = new Id<PipelineBuilder>(new Id(builderId));
            if (!builders.TryGet(id, out _))
                return Result.Failure<ScriptBuilder>(new ResultProblem($"Builder '{builderId}' not found."));

            var script = new ScriptBuilder(request.Script);
            builders.SetScript(id, script);
            return Result.Success(script);
        })
        .WithResultMapping<ScriptBuilder>();

        group.MapGet("/{builderId:guid}/script", Result<ScriptBuilder> (
            PipelineBuilderService builders,
            Guid builderId) =>
        {
            var id = new Id<PipelineBuilder>(new Id(builderId));
            if (!builders.TryGetScript(id, out var script))
                return Result.Failure<ScriptBuilder>(new ResultProblem($"Builder '{builderId}' has no script configuration."));

            return Result.Success(script);
        })
        .WithResultMapping<ScriptBuilder>()
        .AllowAnonymous();

        group.MapDelete("/{builderId:guid}/script", (
            PipelineBuilderService builders,
            Guid builderId) =>
        {
            var id = new Id<PipelineBuilder>(new Id(builderId));
            if (!builders.RemoveScript(id))
                return Result.Failure(new ResultProblem($"Builder '{builderId}' has no script configuration."));

            return Result.Success();
        })
        .WithResultMapping();
    }
}
