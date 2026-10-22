using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Shared.Persistence;

/// <summary><c>pipeline-versions.json</c>: version metadata; each manifest is its own write-once object.</summary>
public record PipelineVersionIndexSnapshot(PipelineVersionMetadata[] Versions);

/// <summary>A <see cref="PipelineVersion"/> without its manifest.</summary>
public record PipelineVersionMetadata(
    Id<PipelineVersion> Id,
    Id<Pipeline> PipelineId,
    int Number,
    string ManifestHash,
    string? CommitSha,
    string BinaryKey,
    IReadOnlyDictionary<string, int> StepVersions,
    IReadOnlyDictionary<string, int> ModelVersions,
    DateTimeOffset CreatedAt)
{
    public static PipelineVersionMetadata From(PipelineVersion v)
        => new(v.Id, v.PipelineId, v.Number, v.ManifestHash, v.CommitSha, v.BinaryKey, v.StepVersions, v.ModelVersions, v.CreatedAt);

    public PipelineVersion WithManifest(string manifest)
        => new(Id, PipelineId, Number, manifest, ManifestHash, CommitSha, BinaryKey, StepVersions, ModelVersions, CreatedAt);
}

/// <summary>
/// <c>pipeline-state.json</c>: per-pipeline operational state (active version/switch, number sequences,
/// blocked steps, the run start queued during a drain, entity-version history, poll-trigger last values).
/// Optional members may be missing from older files (null on load); <see cref="Gates"/> is the former name
/// of <see cref="BlockedSteps"/>, read but no longer written.
/// </summary>
public record PipelineStateSnapshot(
    PipelineActivationEntry[] Activations,
    PipelineSequencesEntry[] Sequences,
    LegacyBlockedStepsEntry[]? Gates = null,
    PendingRunStartEntry[]? PendingStarts = null,
    EntityVersionHistoryEntry[]? EntityVersions = null,
    PollTriggerStatesEntry[]? PollTriggers = null,
    PipelineBlockedStepsEntry[]? BlockedSteps = null);

public record PollTriggerStatesEntry(Id<Pipeline> PipelineId, PollTriggerStates States);

/// <summary>A pipeline's full model/step version history (<see cref="ManifestVersions.History"/>), fed to the next reconcile.</summary>
public record EntityVersionHistoryEntry(Id<Pipeline> PipelineId, EntityVersionMap History);

public record PipelineBlockedStepsEntry(Id<Pipeline> PipelineId, PipelineBlockedSteps Blocked);

public record LegacyBlockedStepsEntry(Id<Pipeline> PipelineId, PipelineBlockedSteps Gates);

public record PendingRunStartEntry(Id<Pipeline> PipelineId, PendingRunStart Pending);

public record PipelineActivationEntry(Id<Pipeline> PipelineId, PipelineActivation Activation);

public record PipelineSequencesEntry(Id<Pipeline> PipelineId, PipelineSequences Sequences);

/// <summary><c>runs.json</c>: which run objects exist (one entry per run; no run content).</summary>
public record RunIndexSnapshot(RunIndexEntry[] Runs);

public record RunIndexEntry(Id<Run> Id, Id<Pipeline> PipelineId, int Number);

/// <summary><c>runs/&lt;p&gt;/&lt;n&gt;/run.json</c>: one run and all its attempts, written together.</summary>
public record RunDocument(Run Run, Attempt[] Attempts);
