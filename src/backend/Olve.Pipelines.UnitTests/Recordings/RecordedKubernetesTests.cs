using System.Text.Json;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;

namespace Olve.Pipelines.UnitTests.Recordings;

/// <summary>A finished Job as a real cluster reports it, through the parsing the service uses.</summary>
public class RecordedKubernetesTests
{
    private static HarnessPodState State(string outcome)
    {
        var job = JsonSerializer.Deserialize(RecordingReplay.Get("k3s", $"job-{outcome}").Response.BodyText, KubernetesJsonContext.Default.K8sJobResponse);
        var pods = JsonSerializer.Deserialize(RecordingReplay.Get("k3s", $"pods-{outcome}").Response.BodyText, KubernetesJsonContext.Default.K8sPodList);
        return HarnessPodState.From(job, pods?.Items.FirstOrDefault());
    }

    [Test]
    public async Task ACompleteJob_IsFinished_AndNotAFailure()
    {
        var state = State("complete");

        await Assert.That(state.JobFailedReason).IsNull();
        await Assert.That(state.Init!.ExitCode).IsEqualTo(0);
        await Assert.That(state.Main!.ExitCode).IsEqualTo(0);
        await Assert.That(state.Main.StartedAt).IsNotNull();
        await Assert.That(state.PodCreatedAt).IsNotNull();
        await Assert.That(HarnessExitCodes.IsFinished(state)).IsTrue();
        await Assert.That(HarnessExitCodes.Classify(state)).IsNull();
    }

    [Test]
    public async Task ADeadlineExceededJob_IsATimeout_ThoughItsPodIsGone()
    {
        var state = State("deadline");

        await Assert.That(state.Main).IsNull();
        await Assert.That(HarnessExitCodes.IsFinished(state)).IsTrue();
        var failure = HarnessExitCodes.Classify(state)!;
        await Assert.That(failure.Kind).IsEqualTo(HarnessFailureKind.DeadlineExceeded);
        await Assert.That(failure.Message).IsEqualTo("step timed out: Job was active longer than specified deadline");
    }
}
