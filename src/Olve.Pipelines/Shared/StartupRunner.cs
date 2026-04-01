namespace Olve.Pipelines.Shared;

public class StartupRunner(IEnumerable<IRunOnStartup> startupServices, ILogger<StartupRunner> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var service in startupServices)
        {
            var result = service.Run();
            if (result.TryPickProblems(out var problems))
            {
                logger.LogError("Startup service {Service} failed: {Problems}", service.GetType().Name, problems);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
