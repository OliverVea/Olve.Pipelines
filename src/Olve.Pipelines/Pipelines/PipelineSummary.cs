using Olve.Pipelines.Pipelines.Building;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// Pre-aggregated view of a pipeline for the list page: enough to render a card
/// (name, optional bound repo, and the per-step health strip) in one round-trip,
/// avoiding an N+1 fan-out of steps+jobs per pipeline from the client.
/// </summary>
public record PipelineSummary(
    Id<Pipeline> Id,
    string Name,
    string? Repo,
    StepHealth[] Steps);

/// <summary>
/// One box in the CI strip: a step and the status of its most recent job. Order
/// is production steps first, then processing steps in pipeline order. <see cref="Status"/>
/// is the job-status discriminator (idle / scheduled / in-progress / done / failed /
/// cancelled / obsolete); "idle" means the step has never run. <see cref="Kind"/> is
/// "production" or "processing".
/// </summary>
public record StepHealth(string Name, string Kind, string Status);
