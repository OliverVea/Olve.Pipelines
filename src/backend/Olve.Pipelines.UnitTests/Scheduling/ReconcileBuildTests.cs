using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared.Storage;
using Olve.Pipelines.UnitTests.Configs;
using Olve.Pipelines.UnitTests.Harness;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>A config commit through the build/describe Job to an active version, over the K8s and storage fakes.</summary>
public class ReconcileBuildTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string ManifestLog =
        $"compiling\n{HarnessSentinels.ManifestBegin}\n{TestFixtureManifest.Text}\n{HarnessSentinels.ManifestEnd}\n";

    private sealed record Ctx(TestRunDomain D, PipelineConfigBinding Binding, PipelineReconcileService Service, FakeKubernetes K8s, FakeStorage Storage)
    {
        public string JobName => PipelineReconcileService.BuildJobName(Binding.PipelineId, Sha);
        public string UrlSecret => BuildJobBuilder.UrlSecretName(JobName);
        public string BinaryKey => StorageLayout.BuildBinaryKey(Binding.PipelineId, Sha);

        public Task<Result<ReconcileReport>> Reconcile(CancellationToken ct = default) => Service.ReconcileAsync(Binding, Sha, ct);
    }

    private static Ctx Build(HarnessOptions? options = null)
    {
        options ??= new HarnessOptions();
        var d = new TestRunDomain().WireEvents(scheduler: true);
        var pipeline = d.CreatePipeline("provisional");
        var binding = new PipelineConfigBinding(Id.New<PipelineConfigBinding>(), pipeline.Id, "acme/widgets", "main", ".pipelines",
            "GITHUB_TOKEN", null, null, ReconcileStatus.NeverRun, DateTimeOffset.UtcNow, WebhookSecret: "hmac-key");
        var k8s = new FakeKubernetes();
        k8s.Logs[BuildJobBuilder.ContainerName] = ManifestLog;
        var storage = new FakeStorage();
        var presigner = new S3UrlPresigner(
            new PresignOptions(new Uri("http://garage.test:3900"), "bucket", "us-east-1", TimeSpan.FromHours(1)),
            new StubS3CredentialsProvider(), TimeProvider.System);
        var control = new HarnessJobControl(k8s, k8s, new KubernetesOptions("runners"), TimeProvider.System,
            new PodChangeSignals(TimeProvider.System), NullLogger<HarnessJobControl>.Instance);
        var service = new PipelineReconcileService(
            new HarnessJobFactory(presigner, options), control, storage, d.VersionService, d.ActivationService, d.Gate, new FakeSecretNames(),
            d.PipelineService, d.EntityVersions, d.SchedulingLock, options, NullLogger<PipelineReconcileService>.Instance)
        {
            PollInterval = TimeSpan.Zero,
            DeletePollInterval = TimeSpan.Zero,
        };
        return new Ctx(d, binding, service, k8s, storage);
    }

    private static HarnessPodState Exited(int code) =>
        new(null, null, null, new HarnessContainerState(BuildJobBuilder.ContainerName, code, "Error", null));

    private static async Task AssertRejected(Ctx c, Result<ReconcileReport> report, string expected)
    {
        await Assert.That(report).Failed();
        report.TryPickProblems(out var problems);
        await Assert.That(problems!.Single().FormattedMessage).IsEqualTo(expected);
        await Assert.That(c.D.VersionService.ListByPipeline(c.Binding.PipelineId)).IsEmpty();
    }

    [Test]
    public async Task Commit_BuildsInAJob_AndTheManifestItPrintedBecomesTheActiveVersion()
    {
        var c = Build();

        var report = await c.Reconcile();

        var version = TestRunDomain.Pick(report).Version;
        await Assert.That(version.Number).IsEqualTo(1);
        await Assert.That(version.BinaryKey).IsEqualTo(c.BinaryKey);
        c.D.ActivationService.TryGet(c.Binding.PipelineId, out var activation);
        await Assert.That(((PipelineActivation.Active)activation!).VersionId).IsEqualTo(version.Id);

        var job = HarnessJobBuilderTests.Serialize(c.K8s.LastJob!);
        await Assert.That(job).Contains(c.JobName);
        await Assert.That(job).Contains("acme/widgets");
        await Assert.That(job).Contains(Sha);
        await Assert.That(job).Contains(PipelineSecrets.Name(c.Binding.PipelineId));
        await Assert.That(c.K8s.UpsertedSecrets).IsEquivalentTo([c.UrlSecret]);
        await Assert.That(c.K8s.DeletedSecrets).IsEquivalentTo([c.UrlSecret]);
    }

    [Test]
    public async Task BuildStillRunning_IsReattached_NotStartedAgain()
    {
        var c = Build();
        c.K8s.Existing = HarnessPodStates.Running;

        await Assert.That(await c.Reconcile()).Succeeded();

        await Assert.That(c.K8s.CreateJobCalls).IsEqualTo(0);
        await Assert.That(c.K8s.DeletedSecrets).IsEquivalentTo([c.UrlSecret]);
    }

    [Test]
    public async Task FinishedBuild_WithItsBinary_IsReused()
    {
        var c = Build();
        c.K8s.Existing = Exited(0);
        c.Storage.Objects[c.BinaryKey] = "step";

        await Assert.That(await c.Reconcile()).Succeeded();

        await Assert.That(c.K8s.CreateJobCalls).IsEqualTo(0);
        await Assert.That(c.K8s.DeletedJobs).IsEmpty();
    }

    [Test]
    [Arguments(0, false)] // the binary is gone, e.g. swept by retention
    [Arguments(1, true)]
    public async Task FinishedBuild_ThatCannotBeReused_IsBuiltAgain(int exitCode, bool binaryStored)
    {
        var c = Build();
        c.K8s.Existing = Exited(exitCode);
        if (binaryStored)
            c.Storage.Objects[c.BinaryKey] = "step";
        c.K8s.States.AddRange([null, Exited(0)]); // gone after the delete, then the new Job finishes

        await Assert.That(await c.Reconcile()).Succeeded();

        await Assert.That(c.K8s.DeletedJobs).IsEquivalentTo([c.JobName]);
        await Assert.That(c.K8s.CreateJobCalls).IsEqualTo(1);
    }

    [Test]
    public async Task FailedBuild_StillBeingDeleted_AsksForARetry()
    {
        var c = Build();
        c.K8s.Existing = Exited(1);
        c.K8s.States.Add(Exited(1));

        var report = await c.Reconcile();

        await AssertRejected(c, report, $"The previous build Job '{c.JobName}' of this commit is still being deleted; retry shortly.");
        report.TryPickProblems(out var problems);
        await Assert.That(problems!.Single()).IsTypeOf<UnavailableProblem>();
        await Assert.That(c.K8s.CreateJobCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(65, "the repository tarball could not be fetched (exit 65)")]
    [Arguments(78, "the pipeline is invalid (exit 78)")]
    [Arguments(64, "olve-build usage error (exit 64)")]
    [Arguments(1, "the build failed (exit 1)")]
    [Arguments(137, "the build exited 137")]
    public async Task FailedBuild_SaysWhy_WithTheLogTail(int exitCode, string reason)
    {
        var c = Build();
        c.K8s.Logs[BuildJobBuilder.ContainerName] = "compiling\nboom\n";
        c.K8s.States.Add(Exited(exitCode));

        var report = await c.Reconcile();

        await AssertRejected(c, report, $"Build/describe failed: {reason}.\ncompiling\nboom");
        await Assert.That(c.K8s.DeletedSecrets).IsEquivalentTo([c.UrlSecret]);
    }

    [Test]
    public async Task FailedBuild_ReportsOnlyTheLast40LogLines()
    {
        var c = Build();
        c.K8s.Logs[BuildJobBuilder.ContainerName] = string.Concat(Enumerable.Range(1, 50).Select(i => $"line {i}\n"));
        c.K8s.States.Add(Exited(1));

        var report = await c.Reconcile();

        var tail = string.Join("\n", Enumerable.Range(11, 40).Select(i => $"line {i}"));
        await AssertRejected(c, report, $"Build/describe failed: the build failed (exit 1).\n{tail}");
    }

    [Test]
    public async Task BuildWithoutAManifest_IsRejected()
    {
        var c = Build();
        c.K8s.Logs[BuildJobBuilder.ContainerName] = "compiling\n";

        await AssertRejected(c, await c.Reconcile(), $"The build/describe Job '{c.JobName}' succeeded but printed no manifest.");
    }

    [Test]
    public async Task BuildThatDisappears_IsRejected()
    {
        var c = Build();
        c.K8s.States.Add(null);

        await AssertRejected(c, await c.Reconcile(), $"The build/describe Job '{c.JobName}' disappeared before it finished.");
        await Assert.That(c.K8s.DeletedSecrets).IsEquivalentTo([c.UrlSecret]);
    }

    [Test]
    public async Task BuildThatOverruns_IsDeletedAndRejected()
    {
        var c = Build(new HarnessOptions { BuildTimeoutSeconds = 0, StartupGraceSeconds = 0 });
        c.K8s.States.Add(HarnessPodStates.Running);

        await AssertRejected(c, await c.Reconcile(), $"The build/describe Job '{c.JobName}' did not finish in time.");
        await Assert.That(c.K8s.DeletedJobs).IsEquivalentTo([c.JobName]);
    }

    // A restart reattaches to the running Job, which still needs its URLs.
    [Test]
    public async Task Shutdown_LeavesTheUrlSecretForTheNextInstance()
    {
        var c = Build();
        using var shutdown = new CancellationTokenSource();
        c.K8s.States.Add(HarnessPodStates.Running);
        c.K8s.OnPoll = shutdown.Cancel;

        await Assert.That(async () => await c.Reconcile(shutdown.Token)).Throws<OperationCanceledException>();

        await Assert.That(c.K8s.DeletedSecrets).IsEmpty();
        await Assert.That(c.K8s.DeletedJobs).IsEmpty();
    }
}
