using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Explicit run cancellation (API / <c>pl</c>). Unlike supersede, an explicit cancel also cancels
/// in-progress attempts — the executor sees the Cancelled status and deletes their K8s Jobs.
/// The run is cancelled first (a terminal run ignores its attempts' cancellation, so it cannot turn
/// Failed), then every Scheduled/InProgress attempt. A run that already finished but still has active
/// attempts (e.g. a superseded run whose deploy is still in flight) gets only its attempts cancelled.
/// <para><see cref="CancelStep"/> cancels one step's active attempt only. A cancelled attempt is treated
/// like any other: the step is settled, its stage's other steps keep running, and once the stage settles
/// the barrier ends the run Cancelled (immediately when the step is alone in its stage). Rerun the step
/// with continue to re-open the run.</para>
/// Transient; under the scheduling lock.
/// </summary>
public class RunCancellationService(
    RunService runs,
    AttemptService attempts,
    RunSchedulingLock schedulingLock,
    ILogger<RunCancellationService> logger)
{
    public Result<Run> Cancel(Id<Run> runId) => schedulingLock.Run<Result<Run>>(() =>
    {
        if (!runs.TryGet(runId, out var run))
            return new NotFoundProblem("Run '{0}' not found.", runId);

        var active = attempts.ListByRun(run.Id).Where(a => a.Status.IsActive()).ToList();
        if (run.Status.IsTerminal() && active.Count == 0)
            return new ConflictProblem("Run #{0} is already {1}.", run.Number, run.Status);

        if (run.Status is RunStatus.Running && runs.Cancel(run.Id).TryPickProblems(out var problems))
            return problems;

        foreach (var attempt in active)
        {
            if (attempts.Cancel(attempt.Id).TryPickProblems(out var cancelProblems))
                logger.LogProblems(LogLevel.Debug, cancelProblems,
                    "Could not cancel attempt {AttemptNumber} of step '{StepName}' in run #{RunNumber}",
                    attempt.Number, attempt.StepName, run.Number);
        }

        logger.LogInformation("Cancelled run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' ({ActiveAttempts} active attempt(s) cancelled)",
            run.Number, run.Id, run.PipelineId, active.Count);

        return runs.TryGet(run.Id, out var current) ? current : run;
    });

    /// <summary>Cancels the Scheduled/InProgress attempt of <paramref name="stepName"/> in a run (its K8s Job is
    /// deleted); the attempt-finished event then advances the run. Also allowed on a finished run that still has the step in flight
    /// (e.g. a superseded run's deploy).</summary>
    public Result<Attempt> CancelStep(Id<Run> runId, string stepName) => schedulingLock.Run<Result<Attempt>>(() =>
    {
        if (!runs.TryGet(runId, out var run))
            return new NotFoundProblem("Run '{0}' not found.", runId);

        var stepAttempts = attempts.ListForStep(run.Id, stepName);
        if (stepAttempts.Count == 0)
            return new NotFoundProblem("Run #{0} has no attempt of step '{1}'.", run.Number, stepName);

        var active = stepAttempts.LastOrDefault(a => a.Status.IsActive());
        if (active is null)
            return new ConflictProblem("Step '{0}' has no scheduled or in-progress attempt in run #{1} (latest is {2}).",
                stepName, run.Number, stepAttempts[^1].Status.Discriminator());

        if (attempts.Cancel(active.Id).TryPickProblems(out var problems))
            return problems;

        logger.LogInformation("Cancelled step '{StepName}' attempt {AttemptNumber} in run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}'",
            stepName, active.Number, run.Number, run.Id, run.PipelineId);

        // The attempt-finished event advances the run (barrier) and checks a pending drain.
        return attempts.TryGet(active.Id, out var current) ? current : active;
    });
}
