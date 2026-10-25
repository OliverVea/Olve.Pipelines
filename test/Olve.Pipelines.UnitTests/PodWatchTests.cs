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
        var wait = signals.WaitAsync("olve-a", TimeSpan.FromMinutes(5), CancellationToken.None);

        signals.Notify("olve-b");
        await Assert.That(wait.IsCompleted).IsFalse();
        signals.Notify("olve-a");

        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);
    }

    [Test]
    public async Task WithoutANotify_TheWaitEndsAtItsTimeout()
    {
        var signals = new PodChangeSignals(TimeProvider.System);

        await signals.WaitAsync("olve-a", TimeSpan.FromMilliseconds(20), CancellationToken.None);
    }

    [Test]
    public async Task TheWatch_SignalsTheJobOfAChangedPod()
    {
        var k8s = new FakeKubernetes();
        var signals = new PodChangeSignals(TimeProvider.System);
        var service = new PodWatchService(k8s, new KubernetesOptions("runners"), signals, NullLogger<PodWatchService>.Instance);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var wait = signals.WaitAsync("olve-a", TimeSpan.FromMinutes(5), CancellationToken.None);

        await k8s.PodChanges.Writer.WriteAsync(new K8sPodItem(new K8sMetadata("olve-a-xyz",
            new Dictionary<string, string> { ["batch.kubernetes.io/job-name"] = "olve-a" })));

        await Assert.That(await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)))).IsEqualTo(wait);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }
}
