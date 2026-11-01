using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.UnitTests;

public class PodWatchTests
{
    [Test]
    public async Task ANotify_WakesTheWaiterBeforeItsTimeout()
    {
        var signals = new PodChangeSignals(TimeProvider.System);
        var wait = signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromMinutes(5), CancellationToken.None);

        signals.Notify("olve-b");
        await Assert.That(wait.IsCompleted).IsFalse();
        signals.Notify("olve-a");

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task WithoutANotify_TheWaitEndsAtItsTimeout()
    {
        var clock = new ManualClock();
        var signals = new PodChangeSignals(clock);
        var wait = signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromSeconds(20), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(19));
        await Assert.That(wait.IsCompleted).IsFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task AForbiddenPodWatch_FallsBackToWatchingJobs()
    {
        var k8s = new FakeKubernetes { ForbidPodWatch = true };
        var signals = new PodChangeSignals(TimeProvider.System);
        var service = new PodWatchService(k8s, new KubernetesOptions("runners"), signals, NullLogger<PodWatchService>.Instance);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var wait = signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromMinutes(5), CancellationToken.None);

        await k8s.JobChanges.Writer.WriteAsync("olve-a");

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        bool[] watches;
        lock (k8s.Watches) watches = [.. k8s.Watches];
        await Assert.That(watches).IsEquivalentTo([true, false]);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task OneWaiterTimingOut_LeavesTheOtherArmed()
    {
        var clock = new ManualClock();
        var signals = new PodChangeSignals(clock);
        var executor = signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromMinutes(5), CancellationToken.None);
        var other = signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromSeconds(10), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(10));
        await other.WaitAsync(TimeSpan.FromSeconds(5));
        signals.Notify("olve-a");

        await executor.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task AChangeBetweenArmingAndWaiting_IsNotMissed()
    {
        var signals = new PodChangeSignals(TimeProvider.System);
        var armed = signals.Arm("olve-a");

        signals.Notify("olve-a");
        var wait = signals.WaitAsync("olve-a", armed, TimeSpan.FromMinutes(5), CancellationToken.None);

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task TheWatch_SignalsTheJobOfAChangedPod()
    {
        var k8s = new FakeKubernetes();
        var signals = new PodChangeSignals(TimeProvider.System);
        var service = new PodWatchService(k8s, new KubernetesOptions("runners"), signals, NullLogger<PodWatchService>.Instance);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var wait = signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromMinutes(5), CancellationToken.None);

        await k8s.JobChanges.Writer.WriteAsync("olve-a");

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }
}
