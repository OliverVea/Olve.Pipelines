using System.Text.Json.Serialization;
using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineSources.Api;

public static class PipelineSourceEndpoints
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(SetGitHubSourceRequest), "github")]
    public abstract record SetPipelineSourceRequest;

    public record SetGitHubSourceRequest(string Name, string Owner, string Repository, string Branch) : SetPipelineSourceRequest;

    public static void MapPipelineSourceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/sources");

        group.MapPost("/", Result<PipelineSource> (
            PipelineService pipelines,
            PipelineSourceService sources,
            Guid pipelineId,
            SetPipelineSourceRequest request) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineSource>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var sourceId = Id.New<PipelineSource>();

            var source = request switch
            {
                SetGitHubSourceRequest gh => (PipelineSource)new GitHubRepositorySource(sourceId, gh.Name, pipelineIdTyped, gh.Owner, gh.Repository, gh.Branch),
                _ => throw new InvalidOperationException("Unknown source type."),
            };

            sources.Set(source);
            return Result.Success(source);
        })
        .WithResultMapping<PipelineSource>();

        group.MapGet("/{sourceId:guid}", Result<PipelineSource> (
            PipelineSourceService sources,
            Guid sourceId) =>
        {
            var sourceIdTyped = new Id<PipelineSource>(new Id(sourceId));

            if (!sources.TryGet(sourceIdTyped, out var source))
            {
                return Result.Failure<PipelineSource>(new ResultProblem($"Source '{sourceId}' not found."));
            }

            return Result.Success(source);
        })
        .WithResultMapping<PipelineSource>()
        .AllowAnonymous();

        group.MapGet("/", Result<PipelineSource[]> (
            PipelineService pipelines,
            PipelineSourceService sources,
            Guid pipelineId) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineSource[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var pipelineSources = sources.GetByPipelineId(pipelineIdTyped).ToArray();
            return Result.Success(pipelineSources);
        })
        .WithResultMapping<PipelineSource[]>()
        .AllowAnonymous();

        group.MapDelete("/{sourceId:guid}", (
            PipelineSourceService sources,
            Guid sourceId) =>
        {
            var sourceIdTyped = new Id<PipelineSource>(new Id(sourceId));

            if (!sources.Delete(sourceIdTyped))
            {
                return Result.Failure(new ResultProblem($"Source '{sourceId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();
    }
}
