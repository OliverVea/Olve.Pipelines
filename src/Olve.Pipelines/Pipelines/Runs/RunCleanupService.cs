using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Runs;

/// <summary>
/// Cascade on deletion: pipeline → its runs; run → its attempts (active ones are cancelled first so
/// any status subscriber sees them end). Garage objects and still-running K8s Jobs are left to
/// retention (B9) and the executor (B5).
/// </summary>
public class RunCleanupService(
    RunService runs,
    AttemptService attempts,
    ILogger<RunCleanupService> logger)
{
    public void HandlePipelineDeleted(Id<Pipeline> pipelineId)
    {
        foreach (var run in runs.ListByPipeline(pipelineId))
        {
            var result = runs.Delete(run.Id);
            if (result.IsError && result.TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Warning, problems,
                    "Failed to delete run #{RunNumber} ('{RunId}') of deleted pipeline '{PipelineId}'",
                    run.Number, run.Id, pipelineId);
        }
    }

    public void HandleRunDeleted(Run run)
    {
        foreach (var attempt in attempts.ListByRun(run.Id))
        {
            if (attempt.Status.IsActive() && attempts.Cancel(attempt.Id).TryPickProblems(out var cancelProblems))
                logger.LogProblems(LogLevel.Warning, cancelProblems,
                    "Failed to cancel attempt {AttemptNumber} of step '{StepName}' in deleted run #{RunNumber} ('{AttemptId}')",
                    attempt.Number, attempt.StepName, run.Number, attempt.Id);

            var result = attempts.Delete(attempt.Id);
            if (result.IsError && result.TryPickProblems(out var problems))
                logger.LogProblems(LogLevel.Warning, problems,
                    "Failed to delete attempt {AttemptNumber} of step '{StepName}' in deleted run #{RunNumber} ('{AttemptId}')",
                    attempt.Number, attempt.StepName, run.Number, attempt.Id);
        }
    }
}
