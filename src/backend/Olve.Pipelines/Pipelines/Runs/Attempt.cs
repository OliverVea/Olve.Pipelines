using System.Text.Json.Serialization;
using Olve.Utilities.Lookup;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// One execution of one step within a run — the unit the job machinery schedules and
/// runs as one K8s Job. Reruns add attempts; <see cref="Number"/> is 1..n per (run, step).
/// <see cref="PipelineId"/> and <see cref="RunNumber"/> are denormalised from the run so the busy key
/// and storage keys never need a run lookup.
/// </summary>
/// <param name="InputHash">Hash of the assembled <c>input.json</c> (kept now for output caching later).</param>
/// <param name="Output">The step's output, present only once <see cref="AttemptStatus.Succeeded"/> with an output.</param>
/// <param name="Input">The assembled <c>input.json</c> (compact JSON, capped at <see cref="AttemptService.MaxInputJsonBytes"/>)
/// the executor uploads for the step; null for attempts the scheduler skipped or failed without running.</param>
/// <param name="Timings">The pod's timestamps, recorded when its Job finished; null before that or when unknown.</param>
/// <param name="Answers">The values given for the step's external inputs, by input name; null when it has none answered.</param>
/// <param name="Links">The external inputs' waiting links, then what the handler linked to (<c>ctx.link</c>): what it made or
/// deployed; null when none.</param>
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
    string? Input = null,
    AttemptTimings? Timings = null,
    IReadOnlyDictionary<string, ExternalAnswer>? Answers = null,
    IReadOnlyList<AttemptLink>? Links = null) : IHasId<Id<Attempt>>
{
    /// <summary>The scheduling key: at most one InProgress attempt per (pipeline, step name).</summary>
    [JsonIgnore]
    public StepKey Key => new(PipelineId, StepName);

    /// <summary>
    /// The step's preflight (null when the step has none): it runs as the attempt's first Job, before the attempt waits
    /// for its external inputs.
    /// </summary>
    public AttemptPreflight? Preflight { get; init; }

    /// <summary>The attempt's next or current Job is its preflight (Scheduled or InProgress).</summary>
    [JsonIgnore]
    public bool InPreflight => Preflight is { PassedAt: null } && Status is AttemptStatus.Scheduled or AttemptStatus.InProgress;

    /// <summary>
    /// Deterministic K8s Job name. Load-bearing: after a restart the executor derives it from the
    /// persisted id to decide between submitting and reattaching. <c>olve-</c> + 32 hex = 37 chars; a preflight's Job
    /// adds <c>-preflight</c>.
    /// </summary>
    [JsonIgnore]
    public string KubernetesJobName => InPreflight ? $"olve-{Id.Value.Value:N}-preflight" : $"olve-{Id.Value.Value:N}";
}

/// <summary>An attempt's preflight: pending until it succeeds (<see cref="PassedAt"/>); <see cref="StartedAt"/> is when its Job started.</summary>
public record AttemptPreflight(DateTimeOffset? PassedAt = null, DateTimeOffset? StartedAt = null);

/// <summary>A link a step reported: an absolute http(s) URL and an optional label.</summary>
public record AttemptLink(string Url, string? Label = null)
{
    public const int MaxCount = 10;
    public const int MaxUrlLength = 2048;
    public const int MaxLabelLength = 100;

    /// <summary><paramref name="first"/>, then the links of <paramref name="then"/> whose URL is not already there.</summary>
    public static IReadOnlyList<AttemptLink> Merge(IReadOnlyList<AttemptLink>? first, IReadOnlyList<AttemptLink> then)
        => first is null or [] ? then : [.. first.Concat(then).DistinctBy(l => l.Url, StringComparer.Ordinal)];
}

/// <summary>A value given for an external input: the JSON as validated (defaults applied), who gave it and when.</summary>
/// <param name="Source">The source kind that answered: <c>person</c>, <c>callback</c> or <c>poll</c>.</param>
public record ExternalAnswer(string Value, string AnsweredBy, DateTimeOffset AnsweredAt, string Source = "person");

/// <summary>When the attempt's pod was created, its init (fetch) and step containers ran, and the server saw it finish.</summary>
public record AttemptTimings(
    DateTimeOffset? PodCreatedAt,
    DateTimeOffset? InitStartedAt,
    DateTimeOffset? InitFinishedAt,
    DateTimeOffset? StepStartedAt,
    DateTimeOffset? StepFinishedAt,
    DateTimeOffset? ObservedAt = null);

/// <summary>
/// Where an attempt's time went, in seconds: <see cref="Queued"/> (created → picked up, less its <see cref="Preflight"/>
/// and <see cref="Input"/>), <see cref="Preflight"/> (its preflight Job, null when it has none or it wasn't timed),
/// <see cref="Input"/> (waiting for its external inputs), then from the pod's
/// timestamps <see cref="PodStart"/> (Job submitted, pod scheduled, sandbox up), <see cref="Fetch"/> (the init
/// container downloading the binary and input), <see cref="Image"/> (pulling and starting the step's image),
/// <see cref="Active"/> (the step container), <see cref="Notice"/> (until the server saw the pod finish) and
/// <see cref="Collect"/> (logs and result collected). Pod phases are null when the attempt has no timings;
/// <see cref="Notice"/> is null for attempts recorded before it was, whose <see cref="Collect"/> includes it.
/// </summary>
public record AttemptPhases(double Queued, double? PodStart, double? Fetch, double? Image, double? Active, double? Collect, double? Notice = null,
    double? Preflight = null, double Input = 0)
{
    public static AttemptPhases? From(Attempt attempt)
    {
        if (attempt.StartedAt() is not { } started)
            return null;
        var t = attempt.Timings;
        var finished = attempt.FinishedAt();
        var preflight = Seconds(attempt.Preflight?.StartedAt, attempt.Preflight?.PassedAt);
        var input = attempt.InputSeconds(attempt.CreatedAt, started);
        return new AttemptPhases(
            Math.Max(0, Seconds(attempt.CreatedAt, started) - (preflight ?? 0) - input),
            Seconds(started, t?.InitStartedAt ?? t?.StepStartedAt),
            Seconds(t?.InitStartedAt, t?.InitFinishedAt),
            Seconds(t?.InitFinishedAt, t?.StepStartedAt),
            Seconds(t?.StepStartedAt, t?.StepFinishedAt),
            Seconds(t?.ObservedAt ?? t?.StepFinishedAt, finished),
            Seconds(t?.StepFinishedAt, t?.ObservedAt),
            preflight,
            input);
    }

    private static double Seconds(DateTimeOffset from, DateTimeOffset to) => Math.Max(0, (to - from).TotalSeconds);

    private static double? Seconds(DateTimeOffset? from, DateTimeOffset? to)
        => from is { } a && to is { } b ? Math.Max(0, (b - a).TotalSeconds) : null;
}

public static class AttemptTimes
{
    /// <summary>When the attempt's first Job started: its preflight's, else its own.</summary>
    public static DateTimeOffset? FirstStartedAt(this Attempt attempt) => attempt.Preflight?.StartedAt ?? attempt.StartedAt();

    /// <summary>
    /// How long the attempt waited for its external inputs (from when it started waiting to its last answer, or to a
    /// decline), within <paramref name="from"/>..<paramref name="to"/> when given.
    /// </summary>
    public static double InputSeconds(this Attempt attempt, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var since = attempt.Preflight?.PassedAt ?? attempt.CreatedAt;
        DateTimeOffset? until = attempt.Status is AttemptStatus.Failed { Kind: AttemptFailureKind.Declined } declined
            ? declined.FinishedAt
            : attempt.Answers is { Count: > 0 } answers ? answers.Values.Max(a => a.AnsweredAt) : null;
        if (until is not { } end)
            return 0;
        var a = from is { } f && f > since ? f : since;
        var b = to is { } t && t < end ? t : end;
        return Math.Max(0, (b - a).TotalSeconds);
    }
}

/// <summary>A succeeded attempt's output: its Garage key and the (size-capped) raw output JSON.</summary>
public record AttemptOutput(string Key, string Json);

/// <summary>Scheduling key: steps are keyed by name (identity), not version.</summary>
public readonly record struct StepKey(Id<Pipeline> PipelineId, string StepName);
