using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests;

public class JobObsoletionServiceTests
{
    // Advancing clock (TestRunDomain default) so each attempt's CreatedAt is distinct and increasing —
    // makes latest-wins supersession deterministic.
    private static TestRunDomain Create() => new TestRunDomain().WireEvents();

    [Test]
    public async Task Attempt_SupersedesPendingForSameKey()
    {
        var d = Create();
        var run1 = d.CreateRun();
        var run2 = d.CreateRun(d.Versions.List().Single());

        var a1 = d.Schedule("build", run1);
        var a2 = d.Schedule("build", run2);

        var updated = d.Get(a1.Id);
        await Assert.That(updated.Status).IsTypeOf<Obsolete>();
        await Assert.That(((Obsolete)updated.Status).SupersedingAttemptId).IsEqualTo(a2.Id);
        await Assert.That(d.Get(a2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task Attempt_DifferentPipelines_NoSuperseding()
    {
        var d = Create();

        var a1 = d.Schedule("build");
        var a2 = d.Schedule("build");

        await Assert.That(d.Get(a1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(d.Get(a2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task Attempt_SamePipelineDifferentStep_NoSuperseding()
    {
        var d = Create();
        var run = d.CreateRun();

        var a1 = d.Schedule("build", run);
        var a2 = d.Schedule("test", run);

        await Assert.That(d.Get(a1.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(d.Get(a2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task Attempt_AfterFirstInProgress_SecondIsQueuedScheduled()
    {
        var d = Create();
        var run = d.CreateRun();

        var a1 = d.Schedule("build", run);
        await Assert.That(d.AttemptService.MarkInProgress(a1.Id)).Succeeded();

        var a2 = d.Schedule("build", run);

        await Assert.That(d.Get(a1.Id).Status).IsTypeOf<InProgress>();
        await Assert.That(d.Get(a2.Id).Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task ThreeScheduled_SameKey_OnlyNewestSurvives_SupersessionChainTerminates()
    {
        var d = Create();
        var run = d.CreateRun();

        var a1 = d.Schedule("build", run);
        var a2 = d.Schedule("build", run);
        var a3 = d.Schedule("build", run);

        await Assert.That(d.Get(a3.Id).Status).IsTypeOf<Scheduled>();
        await Assert.That(d.Get(a1.Id).Status).IsTypeOf<Obsolete>();
        await Assert.That(d.Get(a2.Id).Status).IsTypeOf<Obsolete>();
        await Assert.That(ResolveSurvivor(d, a1.Id)).IsEqualTo(a3.Id);
        await Assert.That(ResolveSurvivor(d, a2.Id)).IsEqualTo(a3.Id);
    }

    // Walk the supersession chain to the single non-obsolete attempt; throws if it loops, so a
    // mutual-supersession cycle fails the test rather than hanging.
    private static Id<Attempt> ResolveSurvivor(TestRunDomain d, Id<Attempt> start)
    {
        var seen = new HashSet<Id<Attempt>>();
        var current = start;
        while (d.Get(current).Status is Obsolete obsolete)
        {
            if (!seen.Add(current))
                throw new InvalidOperationException($"Supersession cycle detected at attempt '{current}'.");
            current = obsolete.SupersedingAttemptId;
        }
        return current;
    }

    // Under concurrent scheduling for one (pipeline, step) key, supersession is a strict total order
    // — exactly one runnable attempt survives and no two attempts name each other.
    [Test]
    public async Task ConcurrentCreations_SameKey_NoMutualSupersession()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var d = Create();
            var run = d.CreateRun();

            using var barrier = new Barrier(2);
            var created = new Id<Attempt>[2];

            void Schedule(int index)
            {
                barrier.SignalAndWait();
                created[index] = d.Schedule("build", run).Id;
            }

            await Task.WhenAll(Task.Run(() => Schedule(0)), Task.Run(() => Schedule(1)));

            var attempts = created.Select(d.Get).ToArray();
            var scheduled = attempts.Where(a => a.Status is Scheduled).ToArray();
            var obsolete = attempts.Where(a => a.Status is Obsolete).ToArray();

            await Assert.That(scheduled).Count().IsEqualTo(1);
            await Assert.That(obsolete).Count().IsEqualTo(1);
            await Assert.That(((Obsolete)obsolete[0].Status).SupersedingAttemptId).IsEqualTo(scheduled[0].Id);
            // Concurrent creation still yields distinct attempt numbers.
            await Assert.That(attempts.Select(a => a.Number).Distinct()).Count().IsEqualTo(2);
        }
    }
}
