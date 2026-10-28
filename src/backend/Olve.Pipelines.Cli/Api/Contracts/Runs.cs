using System.Text.Json;
using System.Text.Json.Nodes;

namespace Olve.Pipelines.Cli.Api.Contracts;

// CLI views of the v2 run API (spec §5, plan B8). Statuses are strings on the wire (enums serialized
// by name; attempt/step statuses are discriminators), so they stay strings here — a new server value
// renders instead of failing deserialization.

/// <summary>A run as listed (<c>ListRuns</c>); links to other runs are by number.</summary>
public class RunSummary
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string Status { get; set; } = "";
    public string TriggerKind { get; set; } = "";
    public int VersionNumber { get; set; }
    public string VersionStamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? HeldAt { get; set; }
    public int? SupersededByNumber { get; set; }
    public int? RerunOfNumber { get; set; }
    public int? ConfigSnapshot { get; set; }
}

public sealed class RunList
{
    public RunSummary[] Items { get; set; } = [];
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int Total { get; set; }
}

/// <summary>What starting a run did: <c>started</c>, <c>deduplicated</c> (with the run) or <c>queued</c>.</summary>
public sealed class RunStart
{
    public string Outcome { get; set; } = "";
    public Run? Run { get; set; }
}

/// <summary>A run in full: the summary's fields plus its version, commit, input, continuability, stages and config.</summary>
public sealed class Run : RunSummary
{
    public Guid VersionId { get; set; }
    public string? CommitSha { get; set; }
    public JsonElement Input { get; set; }
    public bool Continuable { get; set; }
    public RunStageView[] Stages { get; set; } = [];
    /// <summary>The run's effective config (JSON text); null when its version declares none.</summary>
    public string? Config { get; set; }
}

public sealed class RunStageView
{
    public int Index { get; set; }
    public RunStageCondition[] When { get; set; } = [];
    public RunStepView[] Steps { get; set; } = [];
}

public sealed class RunStageCondition
{
    public string Field { get; set; } = "";
    public JsonElement[] Values { get; set; } = [];
}

/// <summary>
/// One step of a run. <see cref="Status"/> is the latest attempt's status, or <c>held</c> (a blocked step holds
/// the run here), <c>pending</c> (not reached yet) or <c>not-run</c> (the run finished without it).
/// </summary>
public sealed class RunStepView
{
    public string Name { get; set; } = "";
    public int? StepVersion { get; set; }
    public string Status { get; set; } = "";
    public string[]? WaitingFor { get; set; }
    public string? Reason { get; set; }
    public int AttemptCount { get; set; }
    public int? LatestAttempt { get; set; }
    public int? SucceededAttempt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Blocked { get; set; }
}

/// <summary>One attempt's output (<c>GetStepOutput</c>); <see cref="Output"/> is absent for a step without an output model.</summary>
public sealed class AttemptOutputResponse
{
    public int Run { get; set; }
    public string Step { get; set; } = "";
    public int Attempt { get; set; }
    public string Status { get; set; } = "";
    public JsonElement? Output { get; set; }
    public string? OutputKey { get; set; }
}

public sealed class AttemptFilesResponse
{
    public int Run { get; set; }
    public string Step { get; set; } = "";
    public int Attempt { get; set; }
    public AttemptFileEntry[] Files { get; set; } = [];
}

public sealed class AttemptFileEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset? LastModified { get; set; }
}

/// <summary>One row of a step's history across runs, newest first (<c>GetStepHistory</c>).</summary>
public sealed class StepHistoryItem
{
    public int RunNumber { get; set; }
    public string RunStatus { get; set; } = "";
    public int VersionNumber { get; set; }
    public string VersionStamp { get; set; } = "";
    public Guid AttemptId { get; set; }
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? StepVersion { get; set; }
    public bool StepVersionChanged { get; set; }
}

/// <summary>Body of <c>RerunStep</c>.</summary>
public sealed class RerunStepRequest
{
    public bool Continue { get; init; }
}

/// <summary>Body for <c>POST …/steps/{step}/input</c>.</summary>
public sealed class AnswerInputRequest
{
    public string? Name { get; init; }
    public required JsonNode Value { get; init; }
}

/// <summary>Body for <c>POST …/steps/{step}/input/decline</c>.</summary>
public sealed class DeclineInputRequest
{
    public string? Comment { get; init; }
}

/// <summary>An error body (<c>ApiProblem</c>): every non-2xx response but 401/403.</summary>
public sealed class ServerProblemBody
{
    public int Status { get; set; }
    public string? Title { get; set; }
    public ServerProblem[]? Errors { get; set; }
}

/// <summary>One error of a <see cref="ServerProblemBody"/>, or of a pre-v2 server's <c>ResultProblem[]</c> body.</summary>
public sealed class ServerProblem
{
    public string Message { get; set; } = "";
    public string? Field { get; set; }
}

/// <summary>The critical path of a range of runs (<c>GetCriticalPath</c>).</summary>
public sealed class CriticalPathReport
{
    public int From { get; set; }
    public int To { get; set; }
    public CriticalPathRun[] Runs { get; set; } = [];
    public CriticalPathStep[] Steps { get; set; } = [];
    public CriticalPathSkip[] Skipped { get; set; } = [];
    public double TotalSeconds { get; set; }
    public double AverageSeconds { get; set; }
}

public sealed class CriticalPathRun
{
    public int Number { get; set; }
    public string Status { get; set; } = "";
    public int VersionNumber { get; set; }
    public string VersionStamp { get; set; } = "";
    public double Seconds { get; set; }
    public double IdleSeconds { get; set; }
    public CriticalPathStage[] Path { get; set; } = [];
    public string[] Notes { get; set; } = [];
    public double? ActiveSeconds { get; set; }
    public CriticalPathLead? Lead { get; set; }
}

/// <summary>The time before a run's first step (automatic runs only).</summary>
public sealed class CriticalPathLead
{
    public double Seconds { get; set; }
    public string Via { get; set; } = "";
    public double? Detect { get; set; }
    public double? Check { get; set; }
    public double? Map { get; set; }
    public double? Start { get; set; }
}

public sealed class CriticalPathStage
{
    public int Stage { get; set; }
    public string Step { get; set; } = "";
    public int Attempts { get; set; }
    public double Seconds { get; set; }
    public double WaitSeconds { get; set; }
    public double? ActiveSeconds { get; set; }
}

public sealed class CriticalPathStep
{
    public string Step { get; set; } = "";
    public int Runs { get; set; }
    public double Seconds { get; set; }
    public double Share { get; set; }
    public double AverageSeconds { get; set; }
    public double WaitSeconds { get; set; }
    public int[] RunNumbers { get; set; } = [];
    public double? OverheadSeconds { get; set; }
}

public sealed class CriticalPathSkip
{
    public int Number { get; set; }
    public string Reason { get; set; } = "";
}
