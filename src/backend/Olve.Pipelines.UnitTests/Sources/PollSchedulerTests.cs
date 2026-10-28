using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Shared.Persistence;
using Olve.Pipelines.Sources;

namespace Olve.Pipelines.UnitTests.Sources;

public class PollSchedulerTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Provider(Func<IEnumerable<PollSource>> sources) : IPollSourceProvider
    {
        public IEnumerable<PollSource> Sources() => sources();
    }

    private static (PollScheduler Scheduler, Clock Clock) Create(params IPollSourceProvider[] providers)
    {
        var clock = new Clock(DateTimeOffset.UnixEpoch);
        return (new PollScheduler(providers, new PersistenceReadiness(), null!, clock, NullLogger<PollScheduler>.Instance), clock);
    }

    [Test]
    public async Task DueSource_RunsOnce_UntilItsIntervalPasses()
    {
        var calls = 0;
        var (scheduler, clock) = Create(new Provider(() => [new PollSource("a", TimeSpan.FromMinutes(1), _ => { calls++; return Task.CompletedTask; })]));

        scheduler.Tick(CancellationToken.None);
        await scheduler.WhenIdle();
        scheduler.Tick(CancellationToken.None);
        await scheduler.WhenIdle();
        await Assert.That(calls).IsEqualTo(1);

        clock.Now += TimeSpan.FromMinutes(1);
        scheduler.Tick(CancellationToken.None);
        await scheduler.WhenIdle();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task RunningSource_IsNotStartedAgain()
    {
        var calls = 0;
        var release = new TaskCompletionSource();
        var (scheduler, clock) = Create(new Provider(() => [new PollSource("slow", TimeSpan.FromSeconds(1), async _ => { calls++; await release.Task; })]));

        scheduler.Tick(CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(5);
        scheduler.Tick(CancellationToken.None);
        release.SetResult();
        await scheduler.WhenIdle();

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task FailingSourceOrProvider_DoesNotStopTheOthers()
    {
        var calls = 0;
        var (scheduler, _) = Create(
            new Provider(() => throw new InvalidOperationException("listing failed")),
            new Provider(() =>
            [
                new PollSource("bad", TimeSpan.FromMinutes(1), _ => throw new InvalidOperationException("check failed")),
                new PollSource("good", TimeSpan.FromMinutes(1), _ => { calls++; return Task.CompletedTask; }),
            ]));

        scheduler.Tick(CancellationToken.None);
        await scheduler.WhenIdle();

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Tick_IsTheShortestInterval_AtMost15Seconds()
    {
        var (fast, _) = Create(new Provider(() => [new PollSource("a", TimeSpan.FromSeconds(2), _ => Task.CompletedTask)]));
        var (slow, _) = Create(new Provider(() => [new PollSource("a", TimeSpan.FromMinutes(15), _ => Task.CompletedTask)]));

        await Assert.That(fast.Tick(CancellationToken.None)).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(slow.Tick(CancellationToken.None)).IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task NewKey_IsDueAtOnce()
    {
        var key = "url-1";
        var calls = new List<string>();
        var (scheduler, _) = Create(new Provider(() => [new PollSource(key, TimeSpan.FromHours(1), _ => { calls.Add(key); return Task.CompletedTask; })]));

        scheduler.Tick(CancellationToken.None);
        await scheduler.WhenIdle();
        key = "url-2";
        scheduler.Tick(CancellationToken.None);
        await scheduler.WhenIdle();

        await Assert.That(calls).IsEquivalentTo(["url-1", "url-2"]);
    }
}
