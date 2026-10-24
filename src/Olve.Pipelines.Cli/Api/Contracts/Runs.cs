using System.Text.Json;

namespace Olve.Pipelines.Cli.Api.Contracts;

// CLI views of the v2 run API (spec §5, plan B8). Statuses are strings on the wire (enums serialized
// by name; attempt/step statuses are discriminators), so they stay strings here — a new server value
// renders instead of failing deserialization.

/// <summary>A run as listed (<c>ListRuns</c>); links to other runs are by number.</summary>
public sealed class RunSummary
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

public sealed class PageOfRunSummary
{
    public RunSummary[] Items { get; set; } = [];
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasNextPage { get; set; }
}

/// <summary>A run entity (returned by cancel/continue and inside <see cref="TriggerRunResponse"/>). <see cref="Input"/> is the stored input JSON text.</summary>
public sealed class Run
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public int Number { get; set; }
    public int VersionNumber { get; set; }
    public string Input { get; set; } = "";
    public string VersionStamp { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? HeldAt { get; set; }
}

/// <summary><c>started</c> (with the run), <c>deduplicated</c> (the existing run) or <c>queued</c> (switching versions; no run yet).</summary>
public sealed class TriggerRunResponse
{
    public string Outcome { get; set; } = "";
    public Run? Run { get; set; }
}

/// <summary>One run in full (<c>GetRun</c>): input, pinned version, and the pinned manifest's stage layout.</summary>
public sealed class RunDetail
{
    public RunSummary Run { get; set; } = new();
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
