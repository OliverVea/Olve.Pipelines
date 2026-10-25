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

        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);
    }

    [Test]
    public async Task ANudgeDuringTheWait_EndsIt_AndIsConsumed()
    {
        var signal = new JobDispatchSignal();
        var wait = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        signal.Nudge();
        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);

        var next = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        await Task.Delay(100);
        await Assert.That(next.IsCompleted).IsFalse();
        signal.Nudge();
        await Assert.That(await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(next);
    }
}
