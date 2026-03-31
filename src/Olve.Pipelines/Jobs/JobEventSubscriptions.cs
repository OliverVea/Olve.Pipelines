namespace Olve.Pipelines.Jobs;

public class JobEventSubscriptions(
    JobEvents events,
    JobObsoletionService obsoletion,
    JobQueueService queue) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        events.OnAdded.Subscribe(obsoletion.HandleJobAdded);
        events.OnAdded.Subscribe(queue.HandleJobAdded);
        events.OnUpdated.Subscribe(queue.HandleJobUpdated);
        events.OnDeleted.Subscribe(queue.HandleJobDeleted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
