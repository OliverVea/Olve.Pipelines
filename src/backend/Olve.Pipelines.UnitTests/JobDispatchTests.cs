using Olve.Pipelines.Jobs;
using static Olve.Pipelines.Jobs.JobPriority;

namespace Olve.Pipelines.UnitTests;

public class JobDispatchTests
{
    /// <summary>A candidate: its name, priority, minutes after the epoch it was created, and whether its own rules hold it.</summary>
    public sealed record Want(string Name, JobPriority Priority, int Minute, bool Held = false);

    public static IEnumerable<Func<(string Rule, int Slots, Want[] Wants, string[] Started)>> Rows()
    {
        yield return () => ("a deploy goes before older checks", 1,
            [new("draft", DraftPull, 0), new("ready", ReadyPull, 1), new("queue", MergeQueue, 2), new("deploy", Deploy, 3)],
            ["deploy"]);
        yield return () => ("each class goes in its order", 5,
            [new("draft", DraftPull, 0), new("outdated", OutdatedPull, 1), new("ready", ReadyPull, 2), new("queue", MergeQueue, 3), new("deploy", Deploy, 4)],
            ["deploy", "queue", "ready", "outdated", "draft"]);
        yield return () => ("a rerun for new checks waits behind a pull request's own run", 1,
            [new("outdated", OutdatedPull, 0), new("ready", ReadyPull, 1)],
            ["ready"]);
        yield return () => ("within a class, oldest first", 2,
            [new("ready-new", ReadyPull, 5), new("ready-old", ReadyPull, 1), new("ready-mid", ReadyPull, 3)],
            ["ready-old", "ready-mid"]);
        yield return () => ("a held candidate leaves its slot to the next", 1,
            [new("deploy-held", Deploy, 0, Held: true), new("ready", ReadyPull, 1)],
            ["ready"]);
        yield return () => ("no free slot, nothing starts", 0,
            [new("deploy", Deploy, 0)],
            []);
        yield return () => ("fewer candidates than slots", 3,
            [new("draft", DraftPull, 0)],
            ["draft"]);
    }

    [Test]
    [MethodDataSource(nameof(Rows))]
    public async Task Dispatch_HandsSlotsOut_ByPriorityThenAge((string Rule, int Slots, Want[] Wants, string[] Started) row)
    {
        var started = new List<string>();
        var candidates = row.Wants.Select(w => new JobCandidate(w.Priority, DateTimeOffset.UnixEpoch.AddMinutes(w.Minute), () =>
        {
            if (w.Held) return false;
            started.Add(w.Name);
            return true;
        }));

        var count = JobDispatch.Run(candidates, () => started.Count < row.Slots);

        await Assert.That(started).IsEquivalentTo(row.Started, TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(row.Rule);
        await Assert.That(count).IsEqualTo(row.Started.Length);
    }
}
