using Microsoft.Extensions.Hosting;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Jobs;

public abstract class JobExecutorBase(
    IServiceScopeFactory scopeFactory,
    JobWatcherRegistry registry,
    IHostApplicationLifetime lifetime,
    ILogger logger) : IJobExecutor
{
    public void EnsureRunning(Id<Attempt> id)
    {
        registry.EnsureRunning(id, async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<IJobExecutor>();
            var attempts = scope.ServiceProvider.GetRequiredService<AttemptService>();

            if (!attempts.TryGet(id, out var attempt))
            {
                logger.LogDebug("Watcher for attempt '{AttemptId}' exiting: attempt not found", id);
                return;
            }

            if (!attempt.Status.IsActive())
            {
                logger.LogDebug("Watcher for attempt '{AttemptId}' ({StepName} #{RunNumber}@{AttemptNumber}) exiting: status is {Status}",
                    id, attempt.StepName, attempt.RunNumber, attempt.Number, attempt.Status.Discriminator());
                return;
            }

            if (executor is not JobExecutorBase typedExecutor)
            {
                throw new InvalidOperationException(
                    $"Resolved executor '{executor.GetType().Name}' is not a JobExecutorBase.");
            }

            try
            {
                await typedExecutor.RunToCompletionAsync(attempt, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                logger.LogInformation("Watcher for attempt '{AttemptId}' cancelled during shutdown — will reattach on next start", id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in watcher for attempt '{AttemptId}'", id);
            }
        });
    }

    protected IServiceScopeFactory ScopeFactory => scopeFactory;

    public void EnsureRunning(Id<CiAttempt> id)
    {
        registry.EnsureRunning(JobWatchKey.Of(id), async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var attempts = scope.ServiceProvider.GetRequiredService<CiAttemptService>();
            if (!attempts.TryGet(id, out var attempt) || !attempt.Status.IsActive())
                return;
            if (scope.ServiceProvider.GetRequiredService<IJobExecutor>() is not JobExecutorBase executor)
                throw new InvalidOperationException("The resolved executor is not a JobExecutorBase.");

            try
            {
                await executor.RunCiToCompletionAsync(attempt, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                logger.LogInformation("Watcher for CI attempt '{AttemptId}' cancelled during shutdown; it reattaches on the next start", id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in watcher for CI attempt '{AttemptId}'", id);
            }
        });
    }

    protected internal abstract Task RunToCompletionAsync(Attempt attempt, CancellationToken ct);

    protected internal abstract Task RunCiToCompletionAsync(CiAttempt attempt, CancellationToken ct);
}
