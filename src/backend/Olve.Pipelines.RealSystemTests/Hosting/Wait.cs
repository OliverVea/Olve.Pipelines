using System.Diagnostics;

namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>Bounded waits on a condition of the real system.</summary>
public static class Wait
{
    public static readonly TimeSpan Cluster = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Run = TimeSpan.FromMinutes(3);

    /// <summary>As <see cref="UntilAsync"/>, but answers with the last value when the budget runs out, for the test to assert on.</summary>
    public static async Task<T> TryUntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value) || clock.Elapsed >= budget)
                return value;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan budget, string what)
    {
        var clock = Stopwatch.StartNew();
        var pause = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            var value = await read();
            if (done(value))
                return value;
            if (clock.Elapsed >= budget)
                throw new TimeoutException($"Waited {budget.TotalSeconds:0}s for {what}; last saw: {value}");
            await Task.Delay(pause);
            pause = TimeSpan.FromMilliseconds(Math.Min(pause.TotalMilliseconds * 1.5, 2000));
        }
    }
}
