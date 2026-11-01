using Olve.Pipelines.Jobs;

namespace Olve.Pipelines.UnitTests;

public class JobDispatchSignalTests
{
    [Test]
    public async Task ANudgeBeforeTheWait_EndsItAtOnce()
    {
        var signal = new JobDispatchSignal();
        signal.Nudge();

        var wait = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ANudgeDuringTheWait_EndsIt_AndIsConsumed()
    {
        var signal = new JobDispatchSignal();
        var wait = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        signal.Nudge();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(signal.IsNudged).IsFalse();

        var next = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        signal.Nudge();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
