using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.RealSystemTests.Hosting;

namespace Olve.Pipelines.RealSystemTests.Contracts;

/// <summary>The Job and pod behaviour the harness executor relies on.</summary>
public abstract class HarnessClusterContract : ContractSuite
{
    public sealed record Cluster(IKubernetesClient Client, IHarnessKubernetesClient Harness);

    protected abstract Task<ContractSubject<Cluster>> SubjectAsync();

    /// <summary>The state of a pod whose init container is done and whose step container is still running.</summary>
    protected abstract Task<HarnessPodState> RunningStateAsync();

    /// <summary>The state of a pod whose containers both exited 0.</summary>
    protected abstract Task<HarnessPodState> ExitedCleanlyStateAsync();

    public static K8sJobManifest SleepJob(string name, int ttlSeconds = 120) => new(
        "batch/v1", "Job",
        new K8sMetadata(name, new() { ["app"] = "olve-pipelines" }),
        new K8sJobSpecBody(
            new K8sPodTemplateSpec(
                new K8sMetadata(name, new() { ["app"] = "olve-pipelines" }),
                new K8sPodSpec([new K8sContainer("step", "alpine:3.22", ["sleep", "30"])])),
            BackoffLimit: 0,
            TtlSecondsAfterFinished: ttlSeconds,
            ActiveDeadlineSeconds: 40));

    [Test]
    public async Task CreateJob_IsTrueThenFalseForTheSameName()
    {
        Rule();
        await using var s = await SubjectAsync();
        var job = SleepJob(s.NewName());

        await Assert.That(await s.Client.Harness.CreateJobAsync(s.Namespace, job)).IsTrue();
        await Assert.That(await s.Client.Harness.CreateJobAsync(s.Namespace, job)).IsFalse();
    }

    [Test]
    public async Task PodState_OfAnUnknownJob_IsNull()
    {
        Rule();
        await using var s = await SubjectAsync();

        await Assert.That(await s.Client.Harness.GetHarnessPodStateAsync(s.Namespace, s.NewName())).IsNull();
    }

    [Test]
    public async Task PodState_OfAnUnknownJob_IsNull_WhileOtherJobsExist()
    {
        Rule();
        await using var s = await SubjectAsync();
        await s.Client.Harness.CreateJobAsync(s.Namespace, SleepJob(s.NewName()));

        await Assert.That(await s.Client.Harness.GetHarnessPodStateAsync(s.Namespace, s.NewName())).IsNull();
    }

    [Test]
    public async Task DeleteJob_MakesItsStateNull_AndAMissingOneIsFine()
    {
        Rule();
        await using var s = await SubjectAsync();
        var job = SleepJob(s.NewName());
        await s.Client.Harness.CreateJobAsync(s.Namespace, job);

        await s.Client.Client.DeleteJobAsync(s.Namespace, job.Metadata.Name);
        await s.Client.Client.DeleteJobAsync(s.Namespace, job.Metadata.Name);

        await Wait.UntilAsync(
            () => s.Client.Harness.GetHarnessPodStateAsync(s.Namespace, job.Metadata.Name),
            state => state is null, Wait.Cluster, "the deleted Job to go");
    }

    [Test]
    public async Task UpsertSecret_CreatesThenReplaces()
    {
        Rule();
        await using var s = await SubjectAsync();
        var name = s.NewName();

        await s.Client.Harness.UpsertSecretAsync(s.Namespace, name, new() { ["URL"] = "one", ["OLD"] = "x" });
        await s.Client.Harness.UpsertSecretAsync(s.Namespace, name, new() { ["URL"] = "two" });

        await Assert.That(await s.Client.Client.GetSecretAsync(s.Namespace, name))
            .IsEquivalentTo(new Dictionary<string, string> { ["URL"] = "two" });
    }

    [Test]
    public async Task Logs_OfAnUnknownJob_AreNull()
    {
        Rule();
        await using var s = await SubjectAsync();

        await Assert.That(await s.Client.Client.GetPodLogsAsync(s.Namespace, s.NewName(), "step")).IsNull();
    }

    [Test]
    public async Task RunningPod_HasADoneInit_AndAnUnfinishedStep()
    {
        Rule();
        var state = await RunningStateAsync();

        await Assert.That(state.Init!.Name).IsEqualTo(HarnessJobBuilder.InitContainerName);
        await Assert.That(state.Init.ExitCode).IsEqualTo(0);
        await Assert.That(state.Init.Reason).IsEqualTo("Completed");
        await Assert.That(state.Main!.Name).IsEqualTo("step");
        await Assert.That(state.Main.Terminated).IsFalse();
        await Assert.That(state.Main.Reason).IsNull();
        await Assert.That(HarnessExitCodes.IsFinished(state)).IsFalse();
        await Assert.That(HarnessExitCodes.IsFatalWaiting(state)).IsFalse();
    }

    [Test]
    public async Task CleanExit_IsFinished_WithNoFailure()
    {
        Rule();
        var state = await ExitedCleanlyStateAsync();

        await Assert.That(state.JobFailedReason).IsNull();
        await Assert.That(state.Main!.ExitCode).IsEqualTo(0);
        await Assert.That(state.Main.Reason).IsEqualTo("Completed");
        await Assert.That(HarnessExitCodes.IsFinished(state)).IsTrue();
        await Assert.That(HarnessExitCodes.Classify(state)).IsNull();
    }
}
