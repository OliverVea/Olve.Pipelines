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

        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);
    }

    [Test]
    public async Task WithoutANotify_TheWaitEndsAtItsTimeout()
    {
        var signals = new PodChangeSignals(TimeProvider.System);

        await signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromMilliseconds(20), CancellationToken.None);
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

        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);
        bool[] watches;
        lock (k8s.Watches) watches = [.. k8s.Watches];
        await Assert.That(watches).IsEquivalentTo([true, false]);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task OneWaiterTimingOut_LeavesTheOtherArmed()
    {
        var signals = new PodChangeSignals(TimeProvider.System);
        var executor = signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromMinutes(5), CancellationToken.None);

        await signals.WaitAsync("olve-a", signals.Arm("olve-a"), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        signals.Notify("olve-a");

        await Assert.That(await Task.WhenAny(executor, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(executor);
    }

    [Test]
    public async Task AChangeBetweenArmingAndWaiting_IsNotMissed()
    {
        var signals = new PodChangeSignals(TimeProvider.System);
        var armed = signals.Arm("olve-a");

        signals.Notify("olve-a");
        var wait = signals.WaitAsync("olve-a", armed, TimeSpan.FromMinutes(5), CancellationToken.None);

        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);
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

        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }
}
