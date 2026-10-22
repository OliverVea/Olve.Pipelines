namespace Olve.Pipelines.Pipelines;

/// <summary>One list-page card: the pipeline, its bound repo and its health over runs.</summary>
public record PipelineSummary(
    Id<Pipeline> Id,
    string Name,
    string? Description,
    string? Repo,
    PipelineHealth Health);
