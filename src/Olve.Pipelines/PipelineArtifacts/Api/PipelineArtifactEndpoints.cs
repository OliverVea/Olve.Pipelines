using Olve.MinimalApi;
using Olve.Pipelines.PipelineBuilds;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineArtifacts.Api;

public static class PipelineArtifactEndpoints
{
    public record CreatePipelineArtifactRequest(string Name);

    public static void MapPipelineArtifactEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/builds/{buildId:guid}/artifacts");

        group.MapPost("/", Result<PipelineArtifact> (
            PipelineBuildService builds,
            PipelineArtifactService artifacts,
            Guid buildId,
            CreatePipelineArtifactRequest request) =>
        {
            var buildIdTyped = new Id<PipelineBuild>(new Id(buildId));

            if (!builds.TryGet(buildIdTyped, out _))
            {
                return Result.Failure<PipelineArtifact>(new ResultProblem($"Build '{buildId}' not found."));
            }

            var artifact = new PipelineArtifact(Id.New<PipelineArtifact>(), request.Name, buildIdTyped);
            artifacts.Set(artifact);
            return Result.Success(artifact);
        })
        .WithResultMapping<PipelineArtifact>();

        group.MapGet("/{artifactId:guid}", Result<PipelineArtifact> (
            PipelineArtifactService artifacts,
            Guid artifactId) =>
        {
            var artifactIdTyped = new Id<PipelineArtifact>(new Id(artifactId));

            if (!artifacts.TryGet(artifactIdTyped, out var artifact))
            {
                return Result.Failure<PipelineArtifact>(new ResultProblem($"Artifact '{artifactId}' not found."));
            }

            return Result.Success(artifact);
        })
        .WithResultMapping<PipelineArtifact>()
        .AllowAnonymous();

        group.MapGet("/", Result<PipelineArtifact[]> (
            PipelineBuildService builds,
            PipelineArtifactService artifacts,
            Guid buildId) =>
        {
            var buildIdTyped = new Id<PipelineBuild>(new Id(buildId));

            if (!builds.TryGet(buildIdTyped, out _))
            {
                return Result.Failure<PipelineArtifact[]>(new ResultProblem($"Build '{buildId}' not found."));
            }

            var buildArtifacts = artifacts.GetByBuildId(buildIdTyped).ToArray();
            return Result.Success(buildArtifacts);
        })
        .WithResultMapping<PipelineArtifact[]>()
        .AllowAnonymous();

        group.MapDelete("/{artifactId:guid}", (
            PipelineArtifactService artifacts,
            Guid artifactId) =>
        {
            var artifactIdTyped = new Id<PipelineArtifact>(new Id(artifactId));

            if (!artifacts.Delete(artifactIdTyped))
            {
                return Result.Failure(new ResultProblem($"Artifact '{artifactId}' not found."));
            }

            return Result.Success();
        })
        .WithResultMapping();
    }
}
