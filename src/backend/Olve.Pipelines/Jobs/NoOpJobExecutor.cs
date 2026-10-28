using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

/// <summary>Test/dev executor: marks the attempt InProgress and waits for <see cref="NoOpJobExecutorPendingStore.Finish"/>.</summary>
public class NoOpJobExecutor(
    IServiceScopeFactory scopeFactory,
    JobWatcherRegistry registry,
    IHostApplicationLifetime lifetime,
    NoOpJobExecutorPendingStore pendingStore,
    AttemptService attempts,
    ILogger<NoOpJobExecutor> logger)
    : JobExecutorBase(scopeFactory, registry, lifetime, logger)
{
    protected internal override async Task RunToCompletionAsync(Attempt attempt, CancellationToken ct)
    {
        logger.LogInformation("No-op execution for attempt '{AttemptId}' of step '{StepName}', awaiting Finish",
            attempt.Id, attempt.StepName);

        LogIfFailed(attempts.MarkInProgress(attempt.Id), attempt.Id);

        var tcs = pendingStore.Register(attempt.Id);

        NoOpJobResult result;
        await using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            result = await tcs.Task;
        }

        switch (result)
        {
            case NoOpJobResult.Success success:
                LogIfFailed(attempts.MarkSucceeded(attempt.Id, success.OutputJson), attempt.Id);
                break;
            case NoOpJobResult.Failure failure:
                LogIfFailed(attempts.MarkFailed(attempt.Id, failure.Reason), attempt.Id);
                break;
            case NoOpJobResult.Skip skip:
                LogIfFailed(attempts.MarkSkipped(attempt.Id, skip.Reason), attempt.Id);
                break;
        }
    }

    private void LogIfFailed<T>(Result<T> result, Id<Attempt> attemptId)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of attempt '{AttemptId}' (it may have been deleted)", attemptId);
    }

    private void LogIfFailed(Result result, Id<Attempt> attemptId)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of attempt '{AttemptId}' (it may have been deleted)", attemptId);
    }
}
