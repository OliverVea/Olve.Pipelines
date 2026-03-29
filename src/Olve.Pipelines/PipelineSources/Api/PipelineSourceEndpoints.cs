using Olve.MinimalApi;
using Olve.Pipelines.Pipelines;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineSources.Api;

public static class PipelineSourceEndpoints
{
    public record CreatePipelineSourceRequest(string Name);
    public record SetHardcodedSourceRequest(Dictionary<string, string> Files);
    public record SetGitHubSourceRequest(string Owner, string Repository, string Branch);

    public static void MapPipelineSourceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/sources");

        group.MapPost("/", Result<PipelineSource> (
            PipelineService pipelines,
            PipelineSourceService sources,
            Guid pipelineId,
            CreatePipelineSourceRequest request) =>
        {
            var pipelineIdTyped = new Id<Pipeline>(new Id(pipelineId));

            if (!pipelines.TryGet(pipelineIdTyped, out _))
            {
                return Result.Failure<PipelineSource>(new ResultProblem($"Pipeline '{pipelineId}' not found."));
            }

            var source = new PipelineSource(Id.New<PipelineSource>(), request.Name, pipelineIdTyped);
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

        // Hardcoded source attachment
        group.MapPut("/{sourceId:guid}/hardcoded", Result<HardcodedSource> (
            PipelineSourceService sources,
            Guid sourceId,
            SetHardcodedSourceRequest request) =>
        {
            var id = new Id<PipelineSource>(new Id(sourceId));
            if (!sources.TryGet(id, out _))
                return Result.Failure<HardcodedSource>(new ResultProblem($"Source '{sourceId}' not found."));

            var hardcoded = new HardcodedSource(request.Files);
            sources.SetHardcoded(id, hardcoded);
            return Result.Success(hardcoded);
        })
        .WithResultMapping<HardcodedSource>();

        group.MapGet("/{sourceId:guid}/hardcoded", Result<HardcodedSource> (
            PipelineSourceService sources,
            Guid sourceId) =>
        {
            var id = new Id<PipelineSource>(new Id(sourceId));
            if (!sources.TryGetHardcoded(id, out var hardcoded))
                return Result.Failure<HardcodedSource>(new ResultProblem($"Source '{sourceId}' has no hardcoded configuration."));

            return Result.Success(hardcoded);
        })
        .WithResultMapping<HardcodedSource>()
        .AllowAnonymous();

        group.MapDelete("/{sourceId:guid}/hardcoded", (
            PipelineSourceService sources,
            Guid sourceId) =>
        {
            var id = new Id<PipelineSource>(new Id(sourceId));
            if (!sources.RemoveHardcoded(id))
                return Result.Failure(new ResultProblem($"Source '{sourceId}' has no hardcoded configuration."));

            return Result.Success();
        })
        .WithResultMapping();

        // GitHub source attachment
        group.MapPut("/{sourceId:guid}/github", Result<GitHubSource> (
            PipelineSourceService sources,
            Guid sourceId,
            SetGitHubSourceRequest request) =>
        {
            var id = new Id<PipelineSource>(new Id(sourceId));
            if (!sources.TryGet(id, out _))
                return Result.Failure<GitHubSource>(new ResultProblem($"Source '{sourceId}' not found."));

            var github = new GitHubSource(request.Owner, request.Repository, request.Branch);
            sources.SetGitHub(id, github);
            return Result.Success(github);
        })
        .WithResultMapping<GitHubSource>();

        group.MapGet("/{sourceId:guid}/github", Result<GitHubSource> (
            PipelineSourceService sources,
            Guid sourceId) =>
        {
            var id = new Id<PipelineSource>(new Id(sourceId));
            if (!sources.TryGetGitHub(id, out var github))
                return Result.Failure<GitHubSource>(new ResultProblem($"Source '{sourceId}' has no GitHub configuration."));

            return Result.Success(github);
        })
        .WithResultMapping<GitHubSource>()
        .AllowAnonymous();

        group.MapDelete("/{sourceId:guid}/github", (
            PipelineSourceService sources,
            Guid sourceId) =>
        {
            var id = new Id<PipelineSource>(new Id(sourceId));
            if (!sources.RemoveGitHub(id))
                return Result.Failure(new ResultProblem($"Source '{sourceId}' has no GitHub configuration."));

            return Result.Success();
        })
        .WithResultMapping();
    }
}
