using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared.Storage;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Triggers;

/// <summary>Event → map-trigger Job → envelope → validated input → run start, over the K8s and storage fakes.</summary>
public class TriggerMappingServiceTests
{
    private sealed record Harness(TriggerMappingService Service, FakeKubernetes K8s, FakeStorage Storage, SchedulerScenario S);

    private static Harness Build(string envelope, TimeProvider? time = null)
    {
        var s = new SchedulerScenario();
        var k8s = new FakeKubernetes();
        k8s.Logs["step"] = $"mapping\n---OLVE-RESULT-BEGIN---\n{envelope}\n---OLVE-RESULT-END---\n";
        var storage = new FakeStorage();
        var presigner = new S3UrlPresigner(
            new PresignOptions(new Uri("http://garage.test:3900"), "bucket", "us-east-1", TimeSpan.FromHours(1)),
            new StubS3CredentialsProvider(), TimeProvider.System);
        var control = new HarnessJobControl(k8s, k8s, new KubernetesOptions("runners"), time ?? TimeProvider.System, new PodChangeSignals(time ?? TimeProvider.System), NullLogger<HarnessJobControl>.Instance);
        var inputs = new RunInputService(s.D.ActivationService, s.D.VersionService, s.D.Plans, s.D.Starts, s.D.Continuations);
        var service = new TriggerMappingService(inputs, s.D.Starts, new HarnessJobFactory(presigner, new HarnessOptions()), control, storage,
            new HarnessOptions(), TimeProvider.System, new IdProvider(), NullLogger<TriggerMappingService>.Instance)
        { PollInterval = TimeSpan.Zero };
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

    // Catches: a mapper abandoned at its deadline, before the startup grace it was promised.
    [Test]
    public async Task AStuckMapper_IsWaitedForUntilItsDeadlinePlusTheGrace_ThenDeleted()
    {
        var time = new MonotonicTimeProvider();
        var h = Build("{}", time);
        var polls = 0;
        h.K8s.States.Add(HarnessPodStates.Running);
        h.K8s.OnPoll = () =>
        {
            polls++;
            time.Advance(TimeSpan.FromSeconds(100)); // the deadline is 300 s and the grace 120 s
        };

        var result = await Map(h);

        await Assert.That(polls).IsEqualTo(5);
        await Assert.That(h.K8s.DeletedJobs).Count().IsEqualTo(1);
        await result.Problems.IsSingle<ResultProblem>($"The map-trigger Job '{h.K8s.DeletedJobs[0]}' for trigger 'poll' did not finish (TimedOut).");
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

        await result.Problems.AreExactly("Trigger 'poll' produced invalid input.", "input.sha: expected string, got number");
        await Assert.That(h.S.D.Runs.List()).IsEmpty();
    }

    [Test]
    public async Task FailedMapper_ReportsItsMessage()
    {
        var h = Build("""{"status":"failed","error":{"kind":"handler","message":"boom"}}""");
        h.K8s.States.Add(new HarnessPodState(null, null, HarnessPodStates.InitOk, new HarnessContainerState("step", 1, "Error", null)));

        var result = await Map(h);

        await result.Problems.IsSingle<ResultProblem>("Trigger 'poll' mapping failed: boom");
    }

    [Test]
    public async Task UnknownTrigger_RunsNoJob()
    {
        var h = Build("""{"status":"skipped","reason":"x"}""");

        await (await Map(h, trigger: "nope")).Problems.IsSingle<ResultProblem>($"Version 1 of pipeline '{h.S.Pipeline.Id}' declares no trigger 'nope'.");
        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(0);
    }

    private const string PollUrl = "https://api.example/head";

    private static readonly PollTrigger Poll = new("poll", PollUrl, new Dictionary<string, string>(), [], "sha", 60);

    private sealed class ConfigGate : IConfigLock
    {
        public bool Current { get; set; } = true;
        public int Calls { get; private set; }

        public async Task<bool> RunIfConfigCurrentAsync(Id<Pipeline> pipelineId, Func<Task> action, CancellationToken ct, bool force = false)
        {
            Calls++;
            if (Current)
                await action();
            return Current;
        }
    }

    private static Func<string, Task> PollValues(Harness h, IConfigLock? gate = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(h.Service);
        if (gate is not null)
            services.AddSingleton(gate);
        var provider = services.BuildServiceProvider();
        var poll = new PollTriggerService(h.S.D.Pipelines, h.S.D.PollTriggers, provider, h.S.D.Time, NullLogger<PollTriggerService>.Instance);
        return value => poll.ApplyValueAsync(provider, h.S.Pipeline, Poll, value, CancellationToken.None);
    }

    private static string? LastValue(Harness h) =>
        h.S.D.PollTriggers.TryGet(h.S.Pipeline.Id, out var states) ? states.Triggers["poll"].LastValue : null;

    private static string Mapped() => $$"""{"status":"succeeded","output":{{SchedulerScenario.Input()}}}""";

    [Test]
    public async Task FirstPolledValue_SeedsWithoutARun()
    {
        var h = Build(Mapped());

        await PollValues(h)("v0");

        await Assert.That(LastValue(h)).IsEqualTo("v0");
        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ChangedPolledValue_MapsThatValue_AndStartsAPollRun(bool configGate)
    {
        var h = Build(Mapped());
        var poll = PollValues(h, configGate ? new ConfigGate() : null);
        await poll("v0");
        var events = new List<string>();
        h.Storage.OnPut = key => { if (key.StartsWith("triggers/")) events.Add(h.Storage.Objects[key]); };

        await poll("v1");

        await Assert.That(events).IsEquivalentTo(new[] { """{"value":"v1"}""" });
        await Assert.That(h.S.D.Runs.List().Single().Trigger.Kind).IsEqualTo(RunTriggerKind.Poll);
        await Assert.That(LastValue(h)).IsEqualTo("v1");
    }

    // The gate can start a reconcile, so a value that hasn't changed must not reach it.
    [Test]
    public async Task UnchangedPolledValue_DoesNotReachTheConfigGate()
    {
        var h = Build(Mapped());
        var gate = new ConfigGate();
        var poll = PollValues(h, gate);
        await poll("v0");
        var calls = gate.Calls;

        await poll("v0");

        await Assert.That(gate.Calls).IsEqualTo(calls);
        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(0);
    }

    // Catches: a change seen while its config commit is unapplied, recorded and so never run.
    [Test]
    public async Task ChangeSeenBeforeItsConfigIsApplied_RunsOnALaterPoll()
    {
        var h = Build(Mapped());
        var gate = new ConfigGate();
        var poll = PollValues(h, gate);
        await poll("v0");

        gate.Current = false;
        await poll("v1");
        await Assert.That(LastValue(h)).IsEqualTo("v0");
        await Assert.That(h.K8s.CreateJobCalls).IsEqualTo(0);

        gate.Current = true;
        await poll("v1");
        await Assert.That(LastValue(h)).IsEqualTo("v1");
        await Assert.That(h.S.D.Runs.List()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ChangedValueWhoseMappingFails_IsNotRecorded()
    {
        var h = Build("""{"status":"succeeded","output":{"sha":42}}""");
        var poll = PollValues(h);
        await poll("v0");

        await poll("v1");

        await Assert.That(LastValue(h)).IsEqualTo("v0");
        await Assert.That(h.S.D.Runs.List()).IsEmpty();
    }
}
