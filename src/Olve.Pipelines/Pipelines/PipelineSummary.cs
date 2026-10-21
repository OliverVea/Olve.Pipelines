namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Pre-aggregated view of a pipeline for the list page: name, optional bound repo, and a health
/// strip in one round-trip. Interim shape between the v1 cut and the v2 run model — the strip is
/// derived from the latest attempt per step name. <see cref="LastChangedAt"/> is the most
/// recent status change across those attempts (null if nothing has run). <see cref="Status"/> is the
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
/// One box in the health strip: a step name and the status of its most recent attempt.
/// <see cref="Status"/> is the attempt-status discriminator (scheduled / in-progress / succeeded /
/// failed / skipped / cancelled / obsolete). <see cref="LastChangedAt"/> is when that attempt last changed status.
/// </summary>
public record StepHealth(string Name, string Status, DateTimeOffset? LastChangedAt);
