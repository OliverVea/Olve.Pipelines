using Microsoft.Extensions.Hosting;
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

    protected internal abstract Task RunToCompletionAsync(Attempt attempt, CancellationToken ct);
}
