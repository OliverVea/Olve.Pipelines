using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Pipelines.Ci;
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

    /// <summary>Marks the CI attempt InProgress and waits for <see cref="NoOpJobExecutorPendingStore.Finish(Id{CiAttempt}, NoOpJobResult)"/>.</summary>
    protected internal override async Task RunCiToCompletionAsync(CiAttempt attempt, CancellationToken ct)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var ciAttempts = scope.ServiceProvider.GetRequiredService<CiAttemptService>();
        LogIfFailed(ciAttempts.MarkInProgress(attempt.Id), attempt.Id);
        var tcs = pendingStore.Register(attempt.Id);
        NoOpJobResult result;
        await using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            result = await tcs.Task;
        }

        LogIfFailed(result switch
        {
            NoOpJobResult.Success success => ciAttempts.MarkSucceeded(attempt.Id, $"ci-noop/{attempt.Id.Value.Value:N}/output.json", success.OutputJson),
            NoOpJobResult.Failure failure => ciAttempts.MarkFailed(attempt.Id, failure.Reason),
            NoOpJobResult.Skip skip => ciAttempts.MarkSkipped(attempt.Id, skip.Reason),
            _ => Result.Success(),
        }, attempt.Id);
    }

    private void LogIfFailed(Result result, Id<CiAttempt> attemptId)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Could not update status of CI attempt '{AttemptId}' (it may have been deleted)", attemptId);
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
