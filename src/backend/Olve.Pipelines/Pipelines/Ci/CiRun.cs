using System.Text.Json.Serialization;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// One CI run of a pull request: its merge commit tested with the pipeline's CI definition. Numbered per pipeline in
/// a sequence of its own. The run uses the binary of <see cref="VersionId"/>, the version active when it started, or
/// with <see cref="OwnDefinition"/> the build of <see cref="TestedSha"/> itself (a <see cref="CiBuild"/>).
/// </summary>
/// <param name="Pull">The pull request it tests.</param>
/// <param name="Draft">The pull request was a draft when the run started (its Jobs go last).</param>
/// <param name="HeadSha">The pull request's head it tested.</param>
/// <param name="TestedSha">GitHub's merge commit of the pull request onto the base branch.</param>
/// <param name="Tree">The tree of <see cref="TestedSha"/>.</param>
/// <param name="MainSha">The base-branch commit it was merged onto (the merge commit's first parent).</param>
/// <param name="Input">The CI input (<c>{repo, sha}</c>) as raw JSON.</param>
/// <param name="Config">The effective config pinned at start (JSON); null when the version declares none.</param>
/// <param name="Problems">Why the run failed before any step ran (its definition can't be used); empty otherwise.</param>
/// <param name="OwnDefinition">The pull request changes the definition, so the run uses the build of its tested commit.</param>
/// <param name="Building">The run waits for that build; its steps start once it is described and its config pinned.</param>
public sealed record CiRun(
    Id<CiRun> Id,
    Id<Pipeline> PipelineId,
    int Number,
    int Pull,
    string Title,
    string Branch,
    bool Draft,
    string HeadSha,
    string TestedSha,
    string Tree,
    string MainSha,
    Id<PipelineVersion> VersionId,
    int VersionNumber,
    string Input,
    RunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null,
    int? SupersededByNumber = null,
    CiCancelReason? CancelReason = null,
    int? ConfigSnapshot = null,
    string? Config = null,
    IReadOnlyList<string>? Problems = null,
    bool OwnDefinition = false,
    bool Building = false) : IHasId<Id<CiRun>>
{
    /// <summary>What a pod sees as <c>OLVE_VERSION_STAMP</c>: <c>ci.&lt;number&gt;</c>.</summary>
    [JsonIgnore]
    public string VersionStamp => $"ci.{Number}";
}

[JsonConverter(typeof(JsonStringEnumConverter<CiCancelReason>))]
public enum CiCancelReason
{
    Closed,
    Merged,
    Dequeued,
}

/// <summary>
/// One execution of one step (or matrix cell) of a CI run: one K8s Job, named <c>olve-ci-&lt;id&gt;</c>, stored under
/// <c>ci/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;attempt&gt;/</c>. CI attempts never wait for input and have no preflight.
/// A reused attempt ran no Job: it succeeded with the output of <see cref="ReusedFrom"/>, whose files and log are its own.
/// </summary>
public sealed record CiAttempt(
    Id<CiAttempt> Id,
    Id<Pipeline> PipelineId,
    Id<CiRun> RunId,
    int RunNumber,
    string StepName,
    int Number,
    AttemptStatus Status,
    DateTimeOffset CreatedAt,
    string? InputHash = null,
    AttemptOutput? Output = null,
    string? Input = null,
    AttemptTimings? Timings = null,
    IReadOnlyList<AttemptLink>? Links = null,
    CiReuse? ReusedFrom = null) : IHasId<Id<CiAttempt>>
{
    public const string JobPrefix = "olve-ci-";

    /// <summary>Deterministic: a restarted executor reattaches to the Job by it.</summary>
    [JsonIgnore]
    public string KubernetesJobName => $"{JobPrefix}{Id.Value.Value:N}";

    [JsonIgnore]
    public string UrlSecretName => $"{KubernetesJobName}-urls";

    /// <summary>Skipped by the scheduler without running (no input), as opposed to a handler skip.</summary>
    [JsonIgnore]
    public bool IsSchedulerSkip => Status is AttemptStatus.Skipped && Input is null;

    public DateTimeOffset? StartedAt() => Status switch
    {
        AttemptStatus.InProgress s => s.StartedAt,
        AttemptStatus.Succeeded s => s.StartedAt,
        AttemptStatus.Failed s => s.StartedAt,
        AttemptStatus.Skipped s => s.StartedAt,
        AttemptStatus.Cancelled s => s.StartedAt,
        _ => null,
    };

    public DateTimeOffset? FinishedAt() => Status switch
    {
        AttemptStatus.Succeeded s => s.FinishedAt,
        AttemptStatus.Failed s => s.FinishedAt,
        AttemptStatus.Skipped s => s.FinishedAt,
        AttemptStatus.Cancelled s => s.CancelledAt,
        _ => null,
    };
}

/// <summary>The attempt a reused one stands for: always one that ran (chains resolve to it), by id and by address.</summary>
public sealed record CiReuse(Id<CiAttempt> AttemptId, int Run, string Step, int Attempt);

public class CiRunEvents
{
    public Event<EntityAdded<CiRun, Id<CiRun>>> OnAdded { get; } = new();
    public Event<EntityUpdated<CiRun, Id<CiRun>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<CiRun, Id<CiRun>>> OnDeleted { get; } = new();
}

public class CiAttemptEvents
{
    public Event<EntityAdded<CiAttempt, Id<CiAttempt>>> OnAdded { get; } = new();
    public Event<EntityUpdated<CiAttempt, Id<CiAttempt>>> OnUpdated { get; } = new();
    public Event<EntityDeleted<CiAttempt, Id<CiAttempt>>> OnDeleted { get; } = new();
}
