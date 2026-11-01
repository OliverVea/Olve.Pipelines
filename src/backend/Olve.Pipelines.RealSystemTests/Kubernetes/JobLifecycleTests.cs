using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.RealSystemTests.Kubernetes;

/// <summary>What a real cluster reports for each way a pod ends, read the way the executor reads it.</summary>
public class JobLifecycleTests
{
    [Test]
    public async Task FailingStep_IsReportedWithItsExitCode()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "exit 3");

        var state = await TestJobs.SubmitAndWaitAsync(server.Cluster, job, s => s.JobFailedReason is not null);

        await Assert.That(state.JobFailedReason).IsEqualTo("BackoffLimitExceeded");
        await Assert.That(state.Main!.ExitCode).IsEqualTo(3);
        await Assert.That(state.Main.Reason).IsEqualTo("Error");
        await Assert.That(HarnessExitCodes.IsFinished(state)).IsTrue();
        await Assert.That((await server.Cluster.Client.GetJobStatusAsync(server.Cluster.Namespace, job.Metadata.Name)).Phase).IsEqualTo(JobPhase.Failed);
    }

    [Test]
    public async Task FailingInit_IsClassifiedAsAnInitFailure()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "true", init: "exit 7");

        var state = await TestJobs.SubmitAndWaitAsync(server.Cluster, job, HarnessExitCodes.IsFinished);

        await Assert.That(state.Init!.ExitCode).IsEqualTo(7);
        var failure = HarnessExitCodes.Classify(state);
        await Assert.That(failure!.Kind).IsEqualTo(AttemptFailureKind.InitFailed);
        await Assert.That(failure.Message).StartsWith("olve-init failed: ");
    }

    [Test]
    public async Task ExceededDeadline_IsClassifiedAsATimeout()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "sleep 30", deadlineSeconds: 2);

        var state = await TestJobs.SubmitAndWaitAsync(server.Cluster, job, s => s.JobFailedReason is not null);

        await Assert.That(state.JobFailedReason).IsEqualTo("DeadlineExceeded");
        var failure = HarnessExitCodes.Classify(state);
        await Assert.That(failure!.Kind).IsEqualTo(AttemptFailureKind.DeadlineExceeded);
        await Assert.That(failure.Message).IsEqualTo("step timed out: Job was active longer than specified deadline");
    }

    [Test]
    public async Task MissingImage_IsAFatalWait()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "true", image: "registry.invalid/no-such-image:1");

        var state = await TestJobs.SubmitAndWaitAsync(server.Cluster, job, HarnessExitCodes.IsFatalWaiting);

        await Assert.That(state.Main!.Reason is "ErrImagePull" or "ImagePullBackOff").IsTrue();
        var failure = HarnessExitCodes.Classify(state);
        await Assert.That(failure!.Kind).IsEqualTo(AttemptFailureKind.PodFailed);
        await Assert.That(failure.Message).StartsWith($"container 'step' cannot start: {state.Main.Reason}");
    }

    [Test]
    public async Task MissingRequiredSecret_IsAFatalWait()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "true",
            env: [K8sEnvVar.FromSecret("VALUE", TestServer.NewName("secret"), "VALUE")]);

        var state = await TestJobs.SubmitAndWaitAsync(server.Cluster, job, HarnessExitCodes.IsFatalWaiting);

        await Assert.That(state.Main!.Reason).IsEqualTo("CreateContainerConfigError");
    }

    [Test]
    public async Task OptionalSecret_LeavesTheVariableUnset()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), """echo "value=[${VALUE-unset}]" """,
            env: [K8sEnvVar.FromSecret("VALUE", TestServer.NewName("secret"), "VALUE", optional: true)]);

        await TestJobs.SubmitAndWaitAsync(server.Cluster, job, HarnessExitCodes.IsFinished);

        await Assert.That(await server.Cluster.Client.GetPodLogsAsync(server.Cluster.Namespace, job.Metadata.Name, "step"))
            .IsEqualTo("value=[unset]\n");
    }

    [Test]
    public async Task Logs_AreReadPerContainer()
    {
        await using var server = await TestServer.LeaseAsync();
        var job = TestJobs.WithInit(TestServer.NewName("job"), "echo from-step", init: "echo from-init");
        await TestJobs.SubmitAndWaitAsync(server.Cluster, job, HarnessExitCodes.IsFinished);
        var cluster = server.Cluster;

        await Assert.That(await cluster.Client.GetPodLogsAsync(cluster.Namespace, job.Metadata.Name, "step")).IsEqualTo("from-step\n");
        await Assert.That(await cluster.Client.GetPodLogsAsync(cluster.Namespace, job.Metadata.Name, HarnessJobBuilder.InitContainerName)).IsEqualTo("from-init\n");
        await Assert.That(() => cluster.Client.GetPodLogsAsync(cluster.Namespace, job.Metadata.Name, "no-such-container")).Throws<HttpRequestException>();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Watch_ReportsAChangeOfTheJob(bool pods)
    {
        await using var server = await TestServer.LeaseAsync();
        var cluster = server.Cluster;
        var name = TestServer.NewName("job");
        using var timeout = new CancellationTokenSource(Wait.Cluster);

        var seen = FirstChangeAsync(cluster, name, pods, timeout.Token);
        await cluster.Client.CreateJobAsync(cluster.Namespace, TestJobs.WithInit(name, "true"));

        await Assert.That(await seen).IsEqualTo(name);
    }

    private static async Task<string> FirstChangeAsync(TestCluster cluster, string name, bool pods, CancellationToken ct)
    {
        await foreach (var change in cluster.Client.WatchJobChangesAsync(cluster.Namespace, "app=olve-pipelines", pods, ct))
        {
            if (change == name)
                return change;
        }
        throw new InvalidOperationException("The watch ended without an event.");
    }

    [Test]
    public async Task JobStatus_OfAMissingJob_IsNullOrThrows()
    {
        await using var server = await TestServer.LeaseAsync();
        var cluster = server.Cluster;
        var name = TestServer.NewName("job");

        await Assert.That(await cluster.Client.TryGetJobStatusAsync(cluster.Namespace, name)).IsNull();
        var thrown = await Assert.That(async () => await cluster.Client.GetJobStatusAsync(cluster.Namespace, name)).Throws<HttpRequestException>();
        await Assert.That(thrown!.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Inventory_ListsJobsAndSecrets_WithoutSecretValues()
    {
        await using var server = await TestServer.LeaseAsync();
        var cluster = server.Cluster;
        var job = TestJobs.WithInit(TestServer.NewName("job"), "true");
        var secret = TestServer.NewName("secret");
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await cluster.Client.CreateSecretAsync(cluster.Namespace, secret, new() { ["KEY"] = "value" });
        await TestJobs.SubmitAndWaitAsync(cluster, job, HarnessExitCodes.IsFinished);

        var finished = await Wait.UntilAsync(
            async () => (await cluster.Client.ListJobsAsync(cluster.Namespace)).Single(j => j.Name == job.Metadata.Name),
            j => j.Finished, Wait.Cluster, "the Job to show as finished");
        var listed = (await cluster.Client.ListSecretsAsync(cluster.Namespace)).Single(s => s.Name == secret);

        await Assert.That(finished.TtlSecondsAfterFinished).IsEqualTo(120);
        await Assert.That(finished.Labels["app"]).IsEqualTo("olve-pipelines");
        await Assert.That(finished.Created).IsGreaterThan(before);
        await Assert.That(listed.Created).IsGreaterThan(before);
    }
}
