using Olve.Pipelines.Jobs;
using static Olve.Pipelines.Jobs.JobPriority;

namespace Olve.Pipelines.UnitTests;

public class JobDispatchTests
{
    /// <summary>A candidate: its name, priority, minutes after the epoch it was created, and whether its own rules hold it.</summary>
    /// <remarks>Every priority but <see cref="Deploy"/> is a CI candidate, under the CI cap.</remarks>
    public sealed record Want(string Name, JobPriority Priority, int Minute, bool Held = false)
    {
        public bool Ci => Priority != Deploy;
    }

    private const int NoCiCap = 99;

    public static IEnumerable<Func<(string Rule, int Slots, int CiSlots, Want[] Wants, string[] Started)>> Rows()
    {
        yield return () => ("a deploy goes before older checks", 1, NoCiCap,
            [new("draft", DraftPull, 0), new("ready", ReadyPull, 1), new("queue", MergeQueue, 2), new("deploy", Deploy, 3)],
            ["deploy"]);
        yield return () => ("each class goes in its order", 5, NoCiCap,
            [new("draft", DraftPull, 0), new("outdated", OutdatedPull, 1), new("ready", ReadyPull, 2), new("queue", MergeQueue, 3), new("deploy", Deploy, 4)],
            ["deploy", "queue", "ready", "outdated", "draft"]);
        yield return () => ("a rerun for new checks waits behind a pull request's own run", 1, NoCiCap,
            [new("outdated", OutdatedPull, 0), new("ready", ReadyPull, 1)],
            ["ready"]);
        yield return () => ("within a class, oldest first", 2, NoCiCap,
            [new("ready-new", ReadyPull, 5), new("ready-old", ReadyPull, 1), new("ready-mid", ReadyPull, 3)],
            ["ready-old", "ready-mid"]);
        yield return () => ("a held candidate leaves its slot to the next", 1, NoCiCap,
            [new("deploy-held", Deploy, 0, Held: true), new("ready", ReadyPull, 1)],
            ["ready"]);
        yield return () => ("no free slot, nothing starts", 0, NoCiCap,
            [new("deploy", Deploy, 0)],
            []);
        yield return () => ("fewer candidates than slots", 3, NoCiCap,
            [new("draft", DraftPull, 0)],
            ["draft"]);
        yield return () => ("CI over its cap waits while a deploy still starts", 4, 1,
            [new("ready-1", ReadyPull, 0), new("ready-2", ReadyPull, 1), new("deploy", Deploy, 2)],
            ["deploy", "ready-1"]);
        yield return () => ("the CI cap counts within the shared slots", 2, 3,
            [new("deploy", Deploy, 0), new("queue", MergeQueue, 1), new("ready", ReadyPull, 2)],
            ["deploy", "queue"]);
        yield return () => ("CI keeps its order under its cap", 5, 2,
            [new("draft", DraftPull, 0), new("outdated", OutdatedPull, 1), new("ready", ReadyPull, 2), new("queue", MergeQueue, 3)],
            ["queue", "ready"]);
    }

    [Test]
    [MethodDataSource(nameof(Rows))]
    public async Task Dispatch_HandsSlotsOut_ByPriorityThenAge((string Rule, int Slots, int CiSlots, Want[] Wants, string[] Started) row)
    {
        var started = new List<string>();
        var ciStarted = 0;
        var candidates = row.Wants.Select(w => new JobCandidate(w.Priority, DateTimeOffset.UnixEpoch.AddMinutes(w.Minute), () =>
        {
            if (w.Held) return false;
            started.Add(w.Name);
            if (w.Ci) ciStarted++;
            return true;
        }, w.Ci ? () => ciStarted < row.CiSlots : null));

        var count = JobDispatch.Run(candidates, () => started.Count < row.Slots);

        await Assert.That(started).IsEquivalentTo(row.Started, TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(row.Rule);
        await Assert.That(count).IsEqualTo(row.Started.Length);
    }
}
