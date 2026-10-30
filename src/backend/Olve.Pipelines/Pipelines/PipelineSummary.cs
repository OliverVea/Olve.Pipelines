using Olve.Pipelines.Pipelines.Runs;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines;

/// <summary>One list-page card: the pipeline, its bound repo, its health over runs and its current version's stages.</summary>
/// <param name="Stages">The current version's barrier stages (the switch target while draining); empty without a version.</param>
public record PipelineSummary(
    Id<Pipeline> Id,
    string Name,
    string? Description,
    string? Repo,
    PipelineHealth Health,
    PipelineStageSummary[] Stages);

/// <summary>One barrier stage with the <c>when</c> conditions enclosing it.</summary>
public record PipelineStageSummary(RunStageCondition[] When, PipelineStepSummary[] Steps);

/// <summary>A step and its latest attempt across runs (<see cref="Status"/> null = never run).</summary>
public record PipelineStepSummary(string Name, Contract.AttemptState? Status, int? RunNumber, DateTimeOffset? At);
