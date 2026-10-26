using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>
/// Poll-trigger last values survive a restart: a fresh <see cref="PollTriggerService"/> over the same
/// (persisted) attachment store compares against the value seen before, instead of re-seeding.
/// </summary>
public class PollTriggerStateTests
{
    private const string Url = "https://api.github.com/repos/o/r/commits/v2";

    private static PollTriggerService Service(TestRunDomain d)
        => new(d.Pipelines, d.PollTriggers, new ServiceCollection().BuildServiceProvider(), d.Time, NullLogger<PollTriggerService>.Instance);

    [Test]
    public async Task FirstValue_Seeds_ThenUnchanged_ThenChanged()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        var poll = Service(d);

        await Assert.That(poll.Observe(p, "poll", Url, "aaa")).IsTypeOf<PollObservation.Seeded>();
        await Assert.That(poll.Observe(p, "poll", Url, "aaa")).IsTypeOf<PollObservation.Unchanged>();
        await Assert.That(poll.Observe(p, "poll", Url, "bbb")).IsEqualTo(new PollObservation.Changed("aaa"));
        await Assert.That(d.PollTriggers.TryGet(p, out var states)).IsTrue();
        await Assert.That(states!.Triggers["poll"].LastValue).IsEqualTo("bbb");
    }

    [Test]
    public async Task AfterRestart_AChangeMadeWhileDown_Triggers()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        Service(d).Observe(p, "poll", Url, "aaa");

        var restarted = Service(d); // new in-memory schedule, same persisted state

        await Assert.That(restarted.Observe(p, "poll", Url, "bbb")).IsEqualTo(new PollObservation.Changed("aaa"));
    }

    [Test]
    public async Task AfterRestart_SameValue_IsUnchanged_AndDoesNotRewriteState()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        Service(d).Observe(p, "poll", Url, "aaa");
        var writes = 0;
        d.PollTriggers.OnSet.Subscribe(_ => writes++);

        await Assert.That(Service(d).Observe(p, "poll", Url, "aaa")).IsTypeOf<PollObservation.Unchanged>();
        await Assert.That(writes).IsEqualTo(0);
    }

    [Test]
    public async Task FailedStart_KeepsTheChangePending_UntilAStartSucceeds()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        var poll = Service(d);
        poll.Observe(p, "poll", Url, "aaa");
        var starts = 0;
        Task<Result> Fail() { starts++; return Task.FromResult(Result.Failure(new ResultProblem("mapping Job failed"))); }
        Task<Result> Succeed() { starts++; return Task.FromResult(Result.Success()); }

        await Assert.That(await poll.ObserveAndStartAsync(p, "poll", Url, "bbb", Fail)).IsTypeOf<PollObservation.StartFailed>();
        await Assert.That(await poll.ObserveAndStartAsync(p, "poll", Url, "bbb", Succeed)).IsEqualTo(new PollObservation.Changed("aaa"));
        await Assert.That(await poll.ObserveAndStartAsync(p, "poll", Url, "bbb", Succeed)).IsTypeOf<PollObservation.Unchanged>();
        await Assert.That(starts).IsEqualTo(2);
    }

    [Test]
    public async Task Seeding_DoesNotStartARun()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        var started = false;

        var observation = await Service(d).ObserveAndStartAsync(p, "poll", Url, "aaa", () => { started = true; return Task.FromResult(Result.Success()); });

        await Assert.That(observation).IsTypeOf<PollObservation.Seeded>();
        await Assert.That(started).IsFalse();
    }

    [Test]
    public async Task ChangedUrl_Reseeds()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        var poll = Service(d);
        poll.Observe(p, "poll", Url, "aaa");

        await Assert.That(poll.Observe(p, "poll", Url + "?x", "bbb")).IsTypeOf<PollObservation.Seeded>();
    }

    [Test]
    public async Task Prune_DropsTriggersNoLongerDeclared_KeepsOthers()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        var poll = Service(d);
        poll.Observe(p, "a", Url, "1");
        poll.Observe(p, "b", Url, "2");

        poll.Prune(p, new HashSet<string> { "b" });
        await Assert.That(d.PollTriggers.TryGet(p, out var states)).IsTrue();
        await Assert.That(states!.Triggers.Keys).IsEquivalentTo(["b"]);

        poll.Prune(p, new HashSet<string>());
        await Assert.That(d.PollTriggers.TryGet(p, out _)).IsFalse();
    }

    [Test]
    public async Task DeletingThePipeline_RemovesItsPollState()
    {
        var d = new TestRunDomain();
        var p = d.CreatePipeline().Id;
        Service(d).Observe(p, "poll", Url, "aaa");

        await Assert.That(d.PipelineService.Delete(p).Succeeded).IsTrue();

        await Assert.That(d.PollTriggers.TryGet(p, out _)).IsFalse();
    }
}
