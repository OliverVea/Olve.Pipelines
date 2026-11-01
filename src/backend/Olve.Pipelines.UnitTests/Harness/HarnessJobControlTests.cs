using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.UnitTests.Harness;

/// <summary>Each poll takes 40 s on the test's clock; a container may stay stuck for 60 s.</summary>
public class HarnessJobControlTests
{
    private static readonly HarnessPodState Stuck = new(null, null, HarnessPodStates.InitOk, new HarnessContainerState("step", null, "ImagePullBackOff", "no such image"));

    private static async Task<(HarnessWait Wait, FakeKubernetes K8s)> Wait(params HarnessPodState[] states)
    {
        var time = new MonotonicTimeProvider();
        var k8s = new FakeKubernetes { Existing = HarnessPodStates.Running };
        k8s.States.AddRange(states);
        k8s.OnPoll = () => time.Advance(TimeSpan.FromSeconds(40));
        var control = new HarnessJobControl(k8s, k8s, new KubernetesOptions("runners"), time, new PodChangeSignals(time), NullLogger<HarnessJobControl>.Instance)
        { FatalWaitingGrace = TimeSpan.FromSeconds(60) };

        return (await control.WaitAsync("job", TimeSpan.Zero, shouldAbort: null, TimeSpan.FromHours(1), CancellationToken.None), k8s);
    }

    // Catches: the grace restarting on every poll, which leaves a stuck Job until its deadline.
    [Test]
    public async Task AContainerStuckPastTheGrace_IsDeleted()
    {
        var (wait, k8s) = await Wait(Stuck);

        await Assert.That(wait is HarnessWait.Finished { DeletedStuck: true }).IsTrue();
        await Assert.That(k8s.DeletedJobs).IsEquivalentTo(["job"]);
    }

    // Catches: time from before the recovery counting towards the grace, which deletes a healthy Job.
    [Test]
    public async Task AContainerThatRecovered_StartsTheGraceAgain()
    {
        var (wait, k8s) = await Wait(Stuck, Stuck, HarnessPodStates.Running, Stuck, Stuck, HarnessPodStates.Exited0);

        await Assert.That(wait is HarnessWait.Finished { DeletedStuck: false }).IsTrue();
        await Assert.That(k8s.DeletedJobs).IsEmpty();
    }
}
