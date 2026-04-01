using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.PipelineSources.Api;

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
            {
                return Result.Failure<PipelineSource>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var source = new PipelineSource(Id.New<PipelineSource>(), request.Name, pipelineId);
            sources.Set(source);
            return Result.Success(source);
        })
        .WithResultMapping<PipelineSource>();

        group.MapGet("/{sourceId}", Result<PipelineSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId) =>
        {
            if (!sources.TryGet(sourceId, out var source))
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
            Id<Pipeline> pipelineId) =>
        {
            if (!pipelines.TryGet(pipelineId, out _))
            {
                return Result.Failure<PipelineSource[]>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var pipelineSources = sources.GetByPipelineId(pipelineId).ToArray();
            return Result.Success(pipelineSources);
        })
        .WithResultMapping<PipelineSource[]>()
        .AllowAnonymous();

        group.MapDelete("/{sourceId}", DeletionResult (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId) =>
            sources.Delete(sourceId))
        .WithDeletionMapping();

        // Hardcoded source attachment
        group.MapPut("/{sourceId}/hardcoded", Result<HardcodedSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId,
            SetHardcodedSourceRequest request) =>
        {
            if (!sources.TryGet(sourceId, out _))
                return Result.Failure<HardcodedSource>(new ResultProblem($"Source '{sourceId}' not found."));

            var hardcoded = new HardcodedSource(request.Files);
            sources.SetHardcoded(sourceId, hardcoded);
            return Result.Success(hardcoded);
        })
        .WithResultMapping<HardcodedSource>();

        group.MapGet("/{sourceId}/hardcoded", Result<HardcodedSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId) =>
        {
            if (!sources.TryGetHardcoded(sourceId, out var hardcoded))
                return Result.Failure<HardcodedSource>(new ResultProblem($"Source '{sourceId}' has no hardcoded configuration."));

            return Result.Success(hardcoded);
        })
        .WithResultMapping<HardcodedSource>()
        .AllowAnonymous();

        group.MapDelete("/{sourceId}/hardcoded", (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId) =>
        {
            if (!sources.RemoveHardcoded(sourceId))
                return Result.Failure(new ResultProblem($"Source '{sourceId}' has no hardcoded configuration."));

            return Result.Success();
        })
        .WithResultMapping();

        // GitHub source attachment
        group.MapPut("/{sourceId}/github", Result<GitHubSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId,
            SetGitHubSourceRequest request) =>
        {
            if (!sources.TryGet(sourceId, out _))
                return Result.Failure<GitHubSource>(new ResultProblem($"Source '{sourceId}' not found."));

            var github = new GitHubSource(request.Owner, request.Repository, request.Branch);
            sources.SetGitHub(sourceId, github);
            return Result.Success(github);
        })
        .WithResultMapping<GitHubSource>();

        group.MapGet("/{sourceId}/github", Result<GitHubSource> (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId) =>
        {
            if (!sources.TryGetGitHub(sourceId, out var github))
                return Result.Failure<GitHubSource>(new ResultProblem($"Source '{sourceId}' has no GitHub configuration."));

            return Result.Success(github);
        })
        .WithResultMapping<GitHubSource>()
        .AllowAnonymous();

        group.MapDelete("/{sourceId}/github", (
            PipelineSourceService sources,
            Id<PipelineSource> sourceId) =>
        {
            if (!sources.RemoveGitHub(sourceId))
                return Result.Failure(new ResultProblem($"Source '{sourceId}' has no GitHub configuration."));

            return Result.Success();
        })
        .WithResultMapping();
    }
}
