using Olve.MinimalApi;
using Olve.Pipelines.PipelineBuilders;
using Olve.Results;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.PipelineArtifacts.Api;

public static class PipelineArtifactEndpoints
{
    public record CreatePipelineArtifactRequest(string Name);

    public static void MapPipelineArtifactEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pipelines/{pipelineId:guid}/builders/{builderId:guid}/artifacts");

        group.MapPost("/", Result<PipelineArtifact> (
            PipelineBuilderService builders,
            PipelineArtifactService artifacts,
            Guid builderId,
            CreatePipelineArtifactRequest request) =>
        {
            var builderIdTyped = new Id<PipelineBuilder>(new Id(builderId));

            if (!builders.TryGet(builderIdTyped, out _))
            {
                return Result.Failure<PipelineArtifact>(new ResultProblem($"Builder '{builderId}' not found."));
            }

            var artifact = new PipelineArtifact(Id.New<PipelineArtifact>(), request.Name, builderIdTyped);
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
            PipelineBuilderService builders,
            PipelineArtifactService artifacts,
            Guid builderId) =>
        {
            var builderIdTyped = new Id<PipelineBuilder>(new Id(builderId));

            if (!builders.TryGet(builderIdTyped, out _))
            {
                return Result.Failure<PipelineArtifact[]>(new ResultProblem($"Builder '{builderId}' not found."));
            }

            var builderArtifacts = artifacts.GetByBuilderId(builderIdTyped).ToArray();
            return Result.Success(builderArtifacts);
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
