using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Runs;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>A CI attempt (<see cref="CiAttempt"/>, stored under <c>ci/</c>) as the Kubernetes executor drives it.</summary>
public sealed class CiHarnessAttempt(CiAttempt attempt, CiAttemptService attempts, CiRunService runs, CiPlanCache plans, CiPersistenceService persistence)
    : IHarnessAttempt
{
    public HarnessAttemptIdentity Identity { get; } = HarnessAttemptIdentity.Of(attempt);

    public int SubmissionAttempts => (attempt.Status as Scheduled)?.SubmissionAttempts ?? 0;

    public bool PassedPreflight => false;

    public bool IsAbandoned() => !attempts.TryGet(attempt.Id, out var current) || current.Status is Cancelled or Obsolete;

    public bool IsInProgress() => attempts.TryGet(attempt.Id, out var current) && current.Status is InProgress;

    public Result<HarnessLaunch> Launch()
    {
        if (!attempts.TryGet(attempt.Id, out var current))
            return new NotFoundProblem("CI attempt '{0}' not found.", attempt.Id);
        if (!runs.TryGet(current.RunId, out var run))
            return new NotFoundProblem("CI run #{0} of attempt '{1}' not found.", current.RunNumber, attempt.Id);
        if (plans.Get(run).TryPickProblems(out var problems, out var plan))
            return problems;
        if (!plan.TryGetUnit(current.StepName, out var stage, out var unit))
            return new ResultProblem("The CI of run #{0} has no step '{1}'.", run.Number, current.StepName);
        if (current.Input is not { } input)
            return new ResultProblem("CI attempt {0} of step '{1}' in run #{2} has no input.", current.Number, current.StepName, run.Number);
        if (!plans.BinaryKey(run, out var binaryKey))
            return new NotFoundProblem("The binary of CI run #{0} is not found.", run.Number);

        return new HarnessLaunch(ConfigDelivery.WithConfig(input, unit.Step, run.Config), binaryKey, run.VersionStamp, unit.Step, stage.Cases,
            unit.Cell, run.VersionNumber);
    }

    public Task<IReadOnlyDictionary<string, string>?> CallbackUrlsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public Result MarkInProgress() => attempts.MarkInProgress(attempt.Id);
    public Result RecordSubmissionFailure() => attempts.RecordSubmissionFailure(attempt.Id);
    public Result MarkFailed(string reason, AttemptFailureKind kind) => attempts.MarkFailed(attempt.Id, reason, kind);
    public Result RecordTimings(AttemptTimings timings) => attempts.Update(attempt.Id, a => a with { Timings = timings });

    public Result AddLinks(IReadOnlyList<AttemptLink> links) =>
        attempts.Update(attempt.Id, a => a with { Links = AttemptLink.Merge(a.Links, links) });

    public Result MarkSucceeded(string? outputJson) => attempts.MarkSucceeded(attempt.Id, Identity.OutputKey, outputJson);
    public Result MarkSkipped(string reason) => attempts.MarkSkipped(attempt.Id, reason);

    public Result<string> FinishPreflight(IReadOnlyList<AttemptLink>? links, IReadOnlyList<string> externalInputs) =>
        new ResultProblem("CI steps have no preflight.");

    public (string Status, TimeSpan? Elapsed)? Finished() =>
        attempts.TryGet(attempt.Id, out var after) ? (after.Status.Discriminator(), after.FinishedAt() - after.StartedAt()) : null;

    public Task<Result> SavedAsync(CancellationToken ct) => persistence.SavedAsync(ct);
}
