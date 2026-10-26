namespace Olve.Pipelines.Shared.Persistence;

/// <summary>
/// Calls <c>flush</c> every <c>interval</c>, one call at a time: a slow flush (a storage outage) delays the
/// next tick instead of stacking calls up behind it.
/// </summary>
internal sealed class FlushTimer : IDisposable
{
    private readonly PeriodicTimer _timer;
    private readonly Task _loop;

    public FlushTimer(Func<Task> flush, ILogger logger, string what, TimeSpan? interval = null, TimeProvider? time = null)
    {
        _timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(1), time ?? TimeProvider.System);
        _loop = Task.Run(async () =>
        {
            while (await _timer.WaitForNextTickAsync())
            {
                try
                {
                    await flush();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled exception while saving {What}", what);
                }
            }
        });
    }

    public void Dispose() => _timer.Dispose();
}
