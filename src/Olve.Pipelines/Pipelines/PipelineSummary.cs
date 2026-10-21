namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Pre-aggregated view of a pipeline for the list page: name, optional bound repo, and a health
/// strip in one round-trip. Interim shape between the v1 cut and the v2 run model — the strip is
/// derived from the latest placeholder job per step name. <see cref="LastChangedAt"/> is the most
/// recent status change across those jobs (null if nothing has run). <see cref="Status"/> is the
/// aggregate health (see <see cref="PipelineStatus"/>).
/// </summary>
public record PipelineSummary(
    Id<Pipeline> Id,
    string Name,
    string? Repo,
    string Status,
    StepHealth[] Steps,
    DateTimeOffset? LastChangedAt);

/// <summary>
/// One box in the health strip: a step name and the status of its most recent job.
/// <see cref="Status"/> is the job-status discriminator (scheduled / in-progress / done / failed /
/// cancelled / obsolete). <see cref="LastChangedAt"/> is when that job last changed status.
/// </summary>
public record StepHealth(string Name, string Status, DateTimeOffset? LastChangedAt);
