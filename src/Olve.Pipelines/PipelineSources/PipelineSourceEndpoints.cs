using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.PipelineSources;

public static class PipelineSourceEndpoints
{
    public record CreatePipelineSourceRequest(string Name);
    public record SetHardcodedSourceRequest(Dictionary<string, string> Files);
    public record SetGitHubSourceRequest(string Owner, string Repository, string Branch);

    public static void MapPipelineSourceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId}/sources");

        group.MapPost("/", Result<PipelineSource> (
            PipelineService pipelines,
            PipelineSourceService sources,
            Id<Pipeline> pipelineId,
            CreatePipelineSourceRequest request) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return Result.Failure<PipelineSource>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

                return sources.Create(pipelineId, request.Name);
            })
            .WithResultMapping<PipelineSource>();

        group.MapGet("/{sourceId}", Result<PipelineSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId)
                => sources.TryGet(sourceId))
            .WithResultMapping<PipelineSource>()
            .AllowAnonymous();

        group.MapGet("/", Result<PipelineSource[]> (
            PipelineService pipelines,
            PipelineSourceService sources,
            Id<Pipeline> pipelineId) =>
            {
                if (!pipelines.TryGet(pipelineId, out _))
                    return Result.Failure<PipelineSource[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));

                return sources.GetByPipelineId(pipelineId);
            })
            .WithResultMapping<PipelineSource[]>()
            .AllowAnonymous();

        group.MapDelete("/{sourceId}", DeletionResult (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId)
                => sources.Delete(sourceId))
            .WithDeletionMapping();

        // Hardcoded source attachment
        group.MapPut("/{sourceId}/hardcoded", Result<HardcodedSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId,
            SetHardcodedSourceRequest request)
                => sources.SetHardcoded(sourceId, new HardcodedSource(request.Files)))
            .WithResultMapping<HardcodedSource>();

        group.MapGet("/{sourceId}/hardcoded", Result<HardcodedSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId)
                => sources.TryGetHardcoded(sourceId))
            .WithResultMapping<HardcodedSource>()
            .AllowAnonymous();

        group.MapDelete("/{sourceId}/hardcoded", Result (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId)
                => sources.RemoveHardcoded(sourceId))
            .WithResultMapping();

        // GitHub source attachment
        group.MapPut("/{sourceId}/github", Result<GitHubSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId,
            SetGitHubSourceRequest request)
                => sources.SetGitHub(sourceId, new GitHubSource(request.Owner, request.Repository, request.Branch)))
            .WithResultMapping<GitHubSource>();

        group.MapGet("/{sourceId}/github", Result<GitHubSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId)
                => sources.TryGetGitHub(sourceId))
            .WithResultMapping<GitHubSource>()
            .AllowAnonymous();

        group.MapDelete("/{sourceId}/github", Result (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId)
                => sources.RemoveGitHub(sourceId))
            .WithResultMapping();
    }
}
