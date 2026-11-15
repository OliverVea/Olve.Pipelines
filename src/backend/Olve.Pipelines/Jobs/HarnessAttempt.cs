using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Jobs;

/// <summary>What one attempt's Job runs: its input, the binary and the step from the pinned definition.</summary>
/// <param name="VersionNumber">The pinned pipeline version, for logs; null when the binary belongs to no version.</param>
public sealed record HarnessLaunch(
    string InputJson,
    string BinaryKey,
    string VersionStamp,
    StepDescriptor Step,
    IReadOnlyList<string> Cases,
    MatrixCell? Cell,
    int? VersionNumber);

/// <summary>
/// The side of an attempt that its kind owns, as <see cref="KubernetesJobExecutor"/> drives it: where its Job lives,
/// what it launches, and its status. One instance per watcher; <see cref="Identity"/> is fixed when it is made.
/// </summary>
public interface IHarnessAttempt
{
    HarnessAttemptIdentity Identity { get; }

    /// <summary>Failed K8s submissions so far, as of when the watcher started.</summary>
    int SubmissionAttempts { get; }

    /// <summary>Its preflight ran and succeeded before this Job: the stored log starts with the preflight's.</summary>
    bool PassedPreflight { get; }

    /// <summary>Gone (its run was deleted), cancelled or made obsolete.</summary>
    bool IsAbandoned();

    bool IsInProgress();

    Result<HarnessLaunch> Launch();

    /// <summary>The run's callback URLs per <c>step/input</c> (<c>OLVE_CALLBACK_URLS</c>); null = none.</summary>
    Task<IReadOnlyDictionary<string, string>?> CallbackUrlsAsync(CancellationToken ct);

    Result MarkInProgress();
    Result RecordSubmissionFailure();
    Result MarkFailed(string reason, AttemptFailureKind kind);
    Result RecordTimings(AttemptTimings timings);
    Result AddLinks(IReadOnlyList<AttemptLink> links);
    Result MarkSucceeded(string? outputJson);
    Result MarkSkipped(string reason);

    /// <summary>The preflight succeeded: the attempt now waits for <paramref name="externalInputs"/>; returns its new status.</summary>
    Result<string> FinishPreflight(IReadOnlyList<AttemptLink>? links, IReadOnlyList<string> externalInputs);

    /// <summary>How long the attempt ran and how it ended, once it has; null when it is gone.</summary>
    (string Status, TimeSpan? Elapsed)? Finished();
}

/// <summary>A CD attempt (<see cref="Attempt"/>, run under <c>runs/</c>) as <see cref="KubernetesJobExecutor"/> drives it.</summary>
public sealed class CdHarnessAttempt(Attempt attempt, AttemptService attempts, AttemptLaunchService launches, CallbackInputUrls callbacks)
    : IHarnessAttempt
{
    private AttemptLaunchSpec? _launch;

    public HarnessAttemptIdentity Identity { get; } = HarnessAttemptIdentity.Of(attempt);

    public int SubmissionAttempts => (attempt.Status as Scheduled)?.SubmissionAttempts ?? 0;

    public bool PassedPreflight => attempt.Preflight is { PassedAt: not null };

    public bool IsAbandoned() => !attempts.TryGet(attempt.Id, out var current) || current.Status is Cancelled or Obsolete;

    public bool IsInProgress() => attempts.TryGet(attempt.Id, out var current) && current.Status is InProgress;

    public Result<HarnessLaunch> Launch()
    {
        if (launches.Get(attempt.Id).TryPickProblems(out var problems, out var launch))
            return problems;
        _launch = launch;
        return new HarnessLaunch(launch.InputJson, launch.Version.BinaryKey, launch.Run.VersionStamp, launch.Step, launch.Cases,
            launch.Cell, launch.Version.Number);
    }

    public async Task<IReadOnlyDictionary<string, string>?> CallbackUrlsAsync(CancellationToken ct) =>
        _launch is { Plan: { } plan } launch ? await callbacks.ForRunAsync(launch.Run.Id, plan, ct) : null;

    public Result MarkInProgress() => Plain(attempts.MarkInProgress(attempt.Id));
    public Result RecordSubmissionFailure() => Plain(attempts.RecordSubmissionFailure(attempt.Id));
    public Result MarkFailed(string reason, AttemptFailureKind kind) => attempts.MarkFailed(attempt.Id, reason, kind);

    public Result RecordTimings(AttemptTimings timings) => attempts.Update(attempt.Id, a => a with { Timings = timings });

    public Result AddLinks(IReadOnlyList<AttemptLink> links) =>
        attempts.Update(attempt.Id, a => a with { Links = AttemptLink.Merge(a.Links, links) });

    public Result MarkSucceeded(string? outputJson) => attempts.MarkSucceeded(attempt.Id, outputJson);
    public Result MarkSkipped(string reason) => attempts.MarkSkipped(attempt.Id, reason);

    public Result<string> FinishPreflight(IReadOnlyList<AttemptLink>? links, IReadOnlyList<string> externalInputs)
    {
        if (attempts.FinishPreflight(attempt.Id, links, externalInputs).TryPickProblems(out var problems, out var after))
            return problems;
        return after.Status.Discriminator();
    }

    public (string Status, TimeSpan? Elapsed)? Finished() =>
        attempts.TryGet(attempt.Id, out var after) ? (after.Status.Discriminator(), after.FinishedAt() - after.StartedAt()) : null;

    private static Result Plain<T>(Result<T> result) => result.TryPickProblems(out var problems) ? Result.Failure(problems) : Result.Success();
}
