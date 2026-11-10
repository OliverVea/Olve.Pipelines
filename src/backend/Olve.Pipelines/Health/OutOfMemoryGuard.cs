namespace Olve.Pipelines.Health;

/// <summary>
/// Ends the process on the first <see cref="OutOfMemoryException"/>, wherever it is thrown or caught,
/// so Kubernetes restarts the pod. A process past its heap limit otherwise stays live (the probes
/// allocate almost nothing) while every other request fails.
/// </summary>
public static class OutOfMemoryGuard
{
    public const string Message = "Out of memory: ending the process so it is restarted.";

    /// <param name="fail">What ending the process is; production passes <see cref="Environment.FailFast(string, Exception)"/>.</param>
    public static IDisposable Install(Action<Exception> fail)
    {
        var tripped = 0;
        // Runs with the heap exhausted: no logging, no allocation, and only once.
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception is OutOfMemoryException && Interlocked.Exchange(ref tripped, 1) == 0)
                fail(e.Exception);
        };
        AppDomain.CurrentDomain.FirstChanceException += handler;
        return new Subscription(handler);
    }

    private sealed class Subscription(EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> handler) : IDisposable
    {
        public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= handler;
    }
}
