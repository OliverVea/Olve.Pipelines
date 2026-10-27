using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared.Storage;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Triggers;

/// <summary>Event → map-trigger Job → envelope → validated input → run start, over the K8s and storage fakes.</summary>
public class TriggerMappingServiceTests
{
    private sealed record Harness(TriggerMappingService Service, FakeKubernetes K8s, FakeStorage Storage, SchedulerScenario S);

    private static Harness Build(string envelope)
    {
        var s = new SchedulerScenario();
        var k8s = new FakeKubernetes();
        k8s.Logs["step"] = $"mapping\n---OLVE-RESULT-BEGIN---\n{envelope}\n---OLVE-RESULT-END---\n";
        var storage = new FakeStorage();
        var presigner = new S3UrlPresigner(
            new PresignOptions(new Uri("http://garage.test:3900"), "bucket", "us-east-1", TimeSpan.FromHours(1)),
            new StubS3CredentialsProvider(), TimeProvider.System);
        var control = new HarnessJobControl(k8s, k8s, new KubernetesOptions("runners"), TimeProvider.System, new PodChangeSignals(TimeProvider.System), NullLogger<HarnessJobControl>.Instance);
        var inputs = new RunInputService(s.D.ActivationService, s.D.VersionService, s.D.Plans, s.D.Starts, s.D.Continuations);
        var service = new TriggerMappingService(inputs, s.D.Starts, new HarnessJobFactory(presigner, new HarnessOptions()), control, storage,
            new HarnessOptions(), TimeProvider.System, NullLogger<TriggerMappingService>.Instance)
        { PollInterval = TimeSpan.FromMilliseconds(5) };
        return new Harness(service, k8s, storage, s);
    }

    private static Task<Olve.Results.Result<RunStartOutcome?>> Map(Harness h, string trigger = "poll") =>
        h.Service.MapAndStartAsync(h.S.Pipeline.Id, trigger, RunTriggerKind.Poll, """{"value":"v1"}""", CancellationToken.None);

    [Test]
    public async Task MappedInput_StartsAnAutomaticRun_AndCleansUp()
    {
        var h = Build($$"""{"status":"succeeded","output":{{SchedulerScenario.Input()}}}""");

        var outcome = TestRunDomain.Pick(await Map(h));

        var run = ((RunStartOutcome.Started)outcome!).Run;
        await Assert.That(run.Trigger.Kind).IsEqualTo(RunTriggerKind.Poll);
        await Assert.That(h.K8s.LastJob!.Spec.Template.Spec.Containers.Single().Command!.TakeLast(2)).IsEquivalentTo(new[] { "map-trigger", "poll" });
        await Assert.That(h.Storage.Objects.Keys.Where(k => k.StartsWith("triggers/"))).IsEmpty();
        await Assert.That(h.K8s.DeletedSecrets).Count().IsEqualTo(1);
    }

    [Test]
    public async Task SkippedMapper_IgnoresTheEvent()
    {
        var h = Build("""{"status":"skipped","reason":"not a release"}""");

        await Assert.That(await Map(h)).SucceededAndValue(v => v.IsNull());
        await Assert.That(h.S.D.Runs.List()).IsEmpty();
    }

    [Test]
    public async Task InvalidMappedInput_IsRejected_AndNothingStarts()
    {
        var h = Build("""{"status":"succeeded","output":{"sha":42}}""");

        var result = await Map(h);

        await Assert.That(result).Failed();
        await Assert.That(string.Join("; ", result.Problems!.Select(p => p.ToString()))).Contains("produced invalid input");
        await Assert.That(h.S.D.Runs.List()).IsEmpty();
    }

    [Test]
    public async Task FailedMapper_ReportsItsMessage()
    {
        var h = Build("""{"status":"failed","error":{"kind":"handler","message":"boom"}}""");
        h.K8s.States.Add(new HarnessPodState(null, null, HarnessPodStates.InitOk, new HarnessContainerState("step", 1, "Error", null)));

        var result = await Map(h);

        await Assert.That(result).Failed();
        await Assert.That(string.Join("; ", result.Problems!.Select(p => p.ToString()))).Contains("boom");
    }

    [Test]
    public async Task UnknownTrigger_RunsNoJob()
    {
        var h = Build("""{"status":"skipped","reason":"x"}""");

        await Assert.That(await Map(h, trigger: "nope")).Failed();
        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(0);
    }
}
