using System.Text.Json.Serialization;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// One execution of one step within a run (spec §5.2) — the unit the job machinery schedules and
/// runs as one K8s Job. Reruns add attempts; <see cref="Number"/> is 1..n per (run, step).
/// <see cref="PipelineId"/> and <see cref="RunNumber"/> are denormalised from the run so the busy key
/// and storage keys never need a run lookup.
/// </summary>
/// <param name="InputHash">Hash of the assembled <c>input.json</c> (kept now for output caching later).</param>
/// <param name="Output">The step's output, present only once <see cref="AttemptStatus.Succeeded"/> with an output.</param>
/// <param name="Input">The assembled <c>input.json</c> (compact JSON, capped at <see cref="AttemptService.MaxInputJsonBytes"/>)
/// the executor uploads for the step; null for attempts the scheduler skipped or failed without running.</param>
public record Attempt(
    Id<Attempt> Id,
    Id<Pipeline> PipelineId,
    Id<Run> RunId,
    int RunNumber,
    string StepName,
    int Number,
    AttemptStatus Status,
    DateTimeOffset CreatedAt,
    string? InputHash = null,
    AttemptOutput? Output = null,
    string? Input = null) : IHasId<Id<Attempt>>
{
    /// <summary>The scheduling key: at most one InProgress attempt per (pipeline, step name).</summary>
    [JsonIgnore]
    public StepKey Key => new(PipelineId, StepName);

    /// <summary>
    /// Deterministic K8s Job name. Load-bearing: after a restart the executor derives it from the
    /// persisted id to decide between submitting and reattaching. <c>olve-</c> + 32 hex = 37 chars.
    /// </summary>
    [JsonIgnore]
    public string KubernetesJobName => $"olve-{Id.Value.Value:N}";
}

/// <summary>A succeeded attempt's output: its Garage key and the (size-capped) raw output JSON.</summary>
public record AttemptOutput(string Key, string Json);

/// <summary>Scheduling key: steps are keyed by name (identity), not version (spec §5.3).</summary>
public readonly record struct StepKey(Id<Pipeline> PipelineId, string StepName);
