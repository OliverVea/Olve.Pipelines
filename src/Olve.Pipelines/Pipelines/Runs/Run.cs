using System.Globalization;
using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// One execution of a pinned pipeline version (spec §5.2): its input plus, via its attempts, every
/// step's output (the pool). <see cref="Number"/> is monotonic per pipeline — the build number that
/// addresses outputs (<c>&lt;pipeline&gt;#&lt;number&gt;/&lt;step&gt;</c>).
/// </summary>
/// <param name="Input">The validated pipeline input as raw JSON.</param>
/// <param name="VersionStamp">Derived from the run: <c>yyyy.MM.dd.&lt;number&gt;</c> (UTC creation date), see <see cref="RunVersionStamp"/>.</param>
/// <param name="SupersededBy">The newer automatic run that superseded this one (status <see cref="RunStatus.Superseded"/>).</param>
/// <param name="RerunOf">The run this one re-runs as a whole (same input, new run).</param>
/// <param name="HeldAt">While Running: the step a promotion gate holds the run at (no attempt created yet).</param>
/// <param name="ResumedAt">When the run was last continued (rerun-and-continue / continue). Failed, cancelled
/// and obsolete attempts created before this are stale and get retried by the scheduler.</param>
public record Run(
    Id<Run> Id,
    Id<Pipeline> PipelineId,
    int Number,
    Id<PipelineVersion> VersionId,
    int VersionNumber,
    string Input,
    string VersionStamp,
    RunStatus Status,
    RunTrigger Trigger,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null,
    Id<Run>? SupersededBy = null,
    Id<Run>? RerunOf = null,
    string? HeldAt = null,
    DateTimeOffset? ResumedAt = null) : IHasId<Id<Run>>;

[JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
public enum RunStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Superseded,
}

[JsonConverter(typeof(JsonStringEnumConverter<RunTriggerKind>))]
public enum RunTriggerKind
{
    Push,
    Poll,
    Manual,
    Webhook,
}

/// <summary>
/// What started a run. <see cref="DedupKey"/> is the automatic-trigger de-duplication key (spec §5.3:
/// pipeline + mapped input); null for explicit (manual) runs, which always create work.
/// </summary>
public record RunTrigger(RunTriggerKind Kind, string? DedupKey = null)
{
    /// <summary>Webhook/poll triggers: de-duplicated and superseding (spec §5.3). Explicit actions are neither.</summary>
    [JsonIgnore]
    public bool IsAutomatic => Kind is RunTriggerKind.Push or RunTriggerKind.Poll or RunTriggerKind.Webhook;
}

public static class RunStatusExtensions
{
    public static bool IsTerminal(this RunStatus status) => status is not RunStatus.Running;
}

/// <summary>The run version stamp, <c>yyyy.MM.dd.&lt;run number&gt;</c> (spec §5.5) — known before any step starts.</summary>
public static class RunVersionStamp
{
    public static string Format(DateTimeOffset createdAt, int runNumber)
        => string.Create(CultureInfo.InvariantCulture,
            $"{createdAt.UtcDateTime.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)}.{runNumber}");

    /// <summary>Extracts the run number from a stamp (its last segment); the full stamp must still be compared.</summary>
    public static bool TryParseRunNumber(string stamp, out int runNumber)
    {
        runNumber = 0;
        var parts = stamp.Split('.');
        return parts.Length == 4
               && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out runNumber)
               && runNumber > 0;
    }
}
