using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Kubernetes;
using static Olve.Pipelines.Jobs.JobPriority;

namespace Olve.Pipelines.UnitTests;

public class ClusterCapacityTests
{
    private const long Gi = 1L << 30;

    private static readonly ResourceAmount Step = new(1000, 3 * Gi / 2);

    private static readonly NodeTaint HelperTaint = new("olve.pro/role", "helper", "NoSchedule");

    private static ClusterNode Node(string name, int cpus, long memGi, bool helper = false, bool ready = true, bool cordoned = false, NodeTaint? taint = null) =>
        new(name, ready, cordoned,
            helper ? [HelperTaint] : taint is null ? [] : [taint],
            helper ? new Dictionary<string, string> { ["olve.pro/role"] = "helper" } : new Dictionary<string, string>(),
            new ResourceAmount(cpus * 1000L, memGi * Gi));

    private static ClusterPod Pod(string? node, int cpuMillis, long memGi, bool ci = false) => new(node, new ResourceAmount(cpuMillis, memGi * Gi), ci);

    private static readonly CapacityOptions Options = new(new ResourceAmount(4000, 12 * Gi), 1.0, ("olve.pro/role", "helper"));

    /// <summary>A pod to place: whether it is CI's, and where it should land (null = it waits).</summary>
    public sealed record Place(bool Ci, string? Node);

    public static IEnumerable<Func<(string Rule, ClusterView View, CapacityOptions Options, Place[] Places)>> PlanRows()
    {
        var bulwark = Node("bulwark-m2", 16, 31);
        var fortress = Node("fortress", 8, 47, helper: true);
        yield return () => ("a step goes to the helper first", new([bulwark, fortress], []), Options,
            [new(false, "fortress")]);
        yield return () => ("the helper full, a step falls back to the other node within its reserve",
            new([bulwark, fortress], [Pod("fortress", 7500, 1)]), Options,
            [new(false, "bulwark-m2")]);
        yield return () => ("the reserve and the apps' requests leave no room: the step waits",
            new([bulwark], [Pod("bulwark-m2", 3500, 18)]), Options,
            [new(false, null)]);
        yield return () => ("each start takes its room, so one pass never hands it out twice",
            new([Node("small", 2, 3, helper: true)], []), Options,
            [new(false, "small"), new(false, "small"), new(false, null)]);
        yield return () => ("a node with a taint steps don't tolerate is never used",
            new([Node("gpu", 32, 128, taint: new("nvidia.com/gpu", "true", "NoSchedule"))], []), Options,
            [new(false, null)]);
        yield return () => ("without the helper setting, the helper's taint isn't tolerated",
            new([fortress], []), Options with { Helper = null },
            [new(false, null)]);
        yield return () => ("a node that isn't Ready is ignored", new([Node("fortress", 8, 47, helper: true, ready: false)], []), Options,
            [new(false, null)]);
        yield return () => ("a cordoned node is ignored", new([Node("fortress", 8, 47, helper: true, cordoned: true)], []), Options,
            [new(false, null)]);
        yield return () => ("a pod not placed yet still takes room", new([Node("small", 2, 3, helper: true)], [Pod(null, 1000, 1)]), Options,
            [new(false, "small"), new(false, null)]);
        yield return () => ("CI's share used up, CI waits while a deploy still starts",
            new([fortress], [Pod("fortress", 3000, 3, ci: true)]), Options with { CiShare = 0.5 },
            [new(true, "fortress"), new(true, null), new(false, "fortress")]);
    }

    [Test]
    [MethodDataSource(nameof(PlanRows))]
    public async Task A_step_starts_only_where_its_request_fits((string Rule, ClusterView View, CapacityOptions Options, Place[] Places) row)
    {
        var plan = CapacityPlan.Create(row.View, row.Options);

        var landed = row.Places.Select(p =>
        {
            var node = plan.Fits(Step, p.Ci);
            if (node is not null) plan.Reserve(node, Step, p.Ci);
            return node;
        }).ToList();

        await Assert.That(landed).IsEquivalentTo(row.Places.Select(p => p.Node), TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(row.Rule);
    }

    [Test]
    [Arguments("1", "1536Mi", 1000L, 1536L << 20)]
    [Arguments("500m", "2Gi", 500L, 2L << 30)]
    [Arguments("2.5", "49332412Ki", 2500L, 49332412L << 10)]
    [Arguments(null, "1G", 0L, 1_000_000_000L)]
    [Arguments("100m", "134217728", 100L, 134217728L)]
    public async Task Kubernetes_quantities_parse(string? cpu, string memory, long millis, long bytes) =>
        await Assert.That(ResourceAmount.Parse(cpu, memory)).IsEqualTo(new ResourceAmount(millis, bytes));

    [Test]
    public async Task Dispatch_starts_the_deploy_and_holds_the_check_when_only_one_fits()
    {
        var plan = CapacityPlan.Create(new ClusterView([Node("fortress", 1, 47, helper: true)], []), Options);
        var started = new List<string>();
        JobCandidate Want(string name, JobPriority priority, int minute, bool ci) =>
            new(priority, DateTimeOffset.UnixEpoch.AddMinutes(minute), () => { started.Add(name); return true; }, Request: Step, Ci: ci);

        JobDispatch.Run([Want("check", ReadyPull, 0, ci: true), Want("deploy", Deploy, 1, ci: false)], () => true, plan);

        await Assert.That(started).IsEquivalentTo(["deploy"]);
    }

    [Test]
    public async Task Dispatch_reattaches_an_attempt_to_its_pod_whatever_room_is_left()
    {
        var plan = CapacityPlan.Create(new ClusterView([Node("fortress", 1, 1, helper: true)], []), Options);
        var started = false;

        JobDispatch.Run([new JobCandidate(Deploy, DateTimeOffset.UnixEpoch, () => started = true)], () => true, plan);

        await Assert.That(started).IsTrue();
    }

    [Test]
    public async Task An_unreadable_cluster_gives_no_plan_so_dispatch_falls_back_to_counting()
    {
        var source = new FakeCapacity { Fail = true };
        var tracker = new ClusterCapacityTracker(source, Options, new Clock(), NullLogger<ClusterCapacityTracker>.Instance);

        await tracker.RefreshAsync(CancellationToken.None);

        await Assert.That(tracker.Plan()).IsNull();
    }

    [Test]
    public async Task A_pod_started_since_the_last_read_still_takes_its_room_until_a_read_can_see_it()
    {
        var source = new FakeCapacity { View = new([Node("small", 2, 3, helper: true)], []) };
        var clock = new Clock();
        var tracker = new ClusterCapacityTracker(source, Options, clock, NullLogger<ClusterCapacityTracker>.Instance);
        await tracker.RefreshAsync(CancellationToken.None);

        var first = tracker.Plan()!;
        first.Reserve(first.Fits(Step, ci: false)!, Step, ci: false);
        var next = tracker.Plan()!;
        await Assert.That(next.Fits(Step, ci: false)).IsEqualTo("small");
        next.Reserve("small", Step, ci: false);
        await Assert.That(tracker.Plan()!.Fits(Step, ci: false)).IsNull();

        clock.Now += ClusterCapacityTracker.ReservationLifetime + TimeSpan.FromSeconds(1);
        await Assert.That(tracker.Plan()!.Fits(Step, ci: false)).IsEqualTo("small");
    }

    [Test]
    public async Task The_cluster_is_read_at_most_every_few_seconds()
    {
        var source = new FakeCapacity { View = new([], []) };
        var clock = new Clock();
        var tracker = new ClusterCapacityTracker(source, Options, clock, NullLogger<ClusterCapacityTracker>.Instance);

        await tracker.RefreshAsync(CancellationToken.None);
        await tracker.RefreshAsync(CancellationToken.None);
        clock.Now += ClusterCapacityTracker.ReadInterval;
        await tracker.RefreshAsync(CancellationToken.None);

        await Assert.That(source.Reads).IsEqualTo(2);
    }

    private sealed class FakeCapacity : IKubernetesCapacity
    {
        public ClusterView View { get; init; } = new([], []);
        public bool Fail { get; init; }
        public int Reads { get; private set; }

        public Task<ClusterView> ReadClusterAsync(CancellationToken ct = default)
        {
            Reads++;
            return Fail ? Task.FromException<ClusterView>(new HttpRequestException("403 Forbidden")) : Task.FromResult(View);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
