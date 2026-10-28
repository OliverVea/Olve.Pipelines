using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>A run as listed (no input, no layout). Links to other runs are by number (the API addresses runs by number).</summary>
public record RunSummary(
    Id<Run> Id,
    int Number,
    RunStatus Status,
    RunTriggerKind TriggerKind,
    int VersionNumber,
    string VersionStamp,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    string? HeldAt,
    int? SupersededByNumber,
    int? RerunOfNumber,
    int? ConfigSnapshot = null);

/// <summary>
/// One run in full (spec §7 run view): its input, pinned version, links, and the stage layout of the
/// pinned version's manifest with every planned step's latest attempt.
/// </summary>
/// <param name="Continuable">True while the run is pinned to the pipeline's active version and not superseded —
/// reruns and continues are possible (spec §6.2: after a switch, re-trigger instead).</param>
/// <param name="Stages">Empty when the pinned version's manifest is unavailable.</param>
/// <param name="Config">The run's effective config (JSON text: the pinned snapshot with the version's defaults); null when the version declares none.</param>
public record RunDetail(
    RunSummary Run,
    Id<PipelineVersion> VersionId,
    string? CommitSha,
    JsonElement Input,
    bool Continuable,
    RunStageView[] Stages,
    string? Config = null);

/// <summary>One barrier stage of the pinned plan, with the <c>when</c> conditions enclosing it (all must match).</summary>
public record RunStageView(int Index, RunStageCondition[] When, RunStepView[] Steps);

/// <summary>A <c>when</c> condition: the pipeline-input <see cref="Field"/> must equal one of <see cref="Values"/>.</summary>
public record RunStageCondition(string Field, JsonElement[] Values);

/// <summary>
/// One step of a run. <see cref="Status"/> is the latest attempt's status discriminator, or — without an
/// attempt — <c>held</c> (the step is blocked and holds the run here), <c>pending</c> (the run has not reached
/// it) or <c>not-run</c> (the run finished without reaching it).
/// </summary>
/// <param name="Reason">The latest attempt's skip or failure reason.</param>
/// <param name="SucceededAttempt">The latest successful attempt — what the address <c>#run/step</c> resolves to.</param>
/// <param name="Blocked">The step is currently blocked.</param>
public record RunStepView(
    string Name,
    int? StepVersion,
    string Status,
    string? Reason,
    int AttemptCount,
    int? LatestAttempt,
    int? SucceededAttempt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool Blocked,
    AttemptPhases? Phases = null)
{
    /// <summary>For a waiting step: the source kinds that may still answer it (<c>person</c>, <c>callback</c>, <c>poll</c>).</summary>
    public string[]? WaitingFor { get; init; }
}

/// <summary>
/// One row of a step's history across runs (spec §6.3), newest first. <see cref="StepVersionChanged"/>
/// marks the first row after a definition change (its step version differs from the next older row's).
/// </summary>
public record StepHistoryItem(
    int RunNumber,
    RunStatus RunStatus,
    int VersionNumber,
    string VersionStamp,
    Id<Attempt> AttemptId,
    int AttemptNumber,
    string Status,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? StepVersion,
    bool StepVersionChanged,
    AttemptPhases? Phases = null);

internal static class JsonFields
{
    /// <summary>A field map as a <see cref="JsonObject"/> (null stays null).</summary>
    public static JsonObject? ToJsonObject(IReadOnlyDictionary<string, JsonElement>? fields)
    {
        if (fields is null)
            return null;
        var obj = new JsonObject();
        foreach (var (key, value) in fields)
            obj[key] = JsonNode.Parse(value.GetRawText());
        return obj;
    }

    public static Dictionary<string, JsonElement> ToDictionary(JsonElement obj) => obj.ValueKind == JsonValueKind.Object
        ? obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
        : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

/// <summary>A stored output file of an attempt (<see cref="Path"/> is the object path: <c>dir.tar</c> for a directory ref <c>dir/</c>).</summary>
public record AttemptFileEntry(string Path, long Size, DateTimeOffset? LastModified);

public record AttemptFilesResponse(int Run, string Step, int Attempt, AttemptFileEntry[] Files);
