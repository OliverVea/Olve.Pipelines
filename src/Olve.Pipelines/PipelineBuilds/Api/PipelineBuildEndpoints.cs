using System.Text.Json.Serialization;
using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineBuilds.Api;

public static class PipelineBuildEndpoints
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(CreateScriptBuildRequest), "script")]
    public abstract record CreatePipelineBuildRequest(string Name);

    public record CreateScriptBuildRequest(string Name, string Script) : CreatePipelineBuildRequest(Name);

    public static void MapPipelineBuildEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/builds");

        group.MapPost("/", Result<PipelineBuild> (
            PipelineService pipelines,
            PipelineBuildService builds,
            Guid pipelineId,
            CreatePipelineBuildRequest request) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineBuild>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var buildId = Id.New<PipelineBuild>();

            var build = request switch
            {
                CreateScriptBuildRequest script => (PipelineBuild)new ScriptBuild(buildId, script.Name, pipelineIdTyped, script.Script),
                _ => throw new InvalidOperationException("Unknown build type."),
            };

            builds.Set(build);
            return Result.Success(build);
        })
        .WithResultMapping<PipelineBuild>();

        group.MapGet("/{buildId:guid}", Result<PipelineBuild> (
            PipelineBuildService builds,
            Guid buildId) =>
        {
            var buildIdTyped = new Id<PipelineBuild>(new Id(buildId));

            if (!builds.TryGet(buildIdTyped, out var build))
            {
                return Result.Failure<PipelineBuild>(new ResultProblem($"Build '{buildId}' not found."));
            }

            return Result.Success(build);
        })
        .WithResultMapping<PipelineBuild>()
        .AllowAnonymous();

        group.MapGet("/", Result<PipelineBuild[]> (
            PipelineService pipelines,
            PipelineBuildService builds,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineBuild[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var pipelineBuilds = builds.GetByPipelineId(pipelineIdTyped).ToArray();
            return Result.Success(pipelineBuilds);
        })
        .WithResultMapping<PipelineBuild[]>()
        .AllowAnonymous();

        group.MapDelete("/{buildId:guid}", (
            PipelineBuildService builds,
            Guid buildId) =>
        {
            var buildIdTyped = new Id<PipelineBuild>(new Id(buildId));

            if (!builds.Delete(buildIdTyped))
            {
                return Result.Failure(new ResultProblem($"Build '{buildId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();
    }
}
