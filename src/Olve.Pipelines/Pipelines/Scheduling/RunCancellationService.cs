using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Explicit run cancellation (API / <c>pl</c>). Unlike supersede, an explicit cancel also cancels
/// in-progress attempts — the executor sees the Cancelled status and deletes their K8s Jobs.
/// The run is cancelled first (a terminal run ignores its attempts' cancellation, so it cannot turn
/// Failed), then every Scheduled/InProgress attempt. A run that already finished but still has active
/// attempts (e.g. a superseded run whose deploy is still in flight) gets only its attempts cancelled.
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
            return new ResultProblem("Run '{0}' not found.", runId);

        var active = attempts.ListByRun(run.Id).Where(a => a.Status.IsActive()).ToList();
        if (run.Status.IsTerminal() && active.Count == 0)
            return new ResultProblem("Run #{0} is already {1}.", run.Number, run.Status);

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
}
