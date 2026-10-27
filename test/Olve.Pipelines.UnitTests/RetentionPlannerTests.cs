using Microsoft.Extensions.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly RetentionOptions Options = RetentionOptions.Defaults with { DryRun = false };

    private static readonly Id<Pipeline> P = new(new Id(new Guid("11111111-2222-3333-4444-555555555555")));
    private static readonly Id<Pipeline> Q = new(new Id(new Guid("66666666-7777-8888-9999-000000000000")));
    private static string Hex(Id<Pipeline> p) => p.Value.Value.ToString("N");

    private static Run Run(Id<Pipeline> p, int number, double ageDays, RunStatus status = RunStatus.Succeeded, Id<PipelineVersion>? version = null)
    {
        var at = Now - TimeSpan.FromDays(ageDays);
        return new Run(Id.New<Run>(), p, number, version ?? Id.New<PipelineVersion>(), 1, "{}", "s", status,
            new RunTrigger(RunTriggerKind.Manual), at, status.IsTerminal() ? at : null);
    }

    private static StoredObject Obj(string key, double ageDays = 0, long size = 10)
        => new(key, Now - TimeSpan.FromDays(ageDays), size);

    private static RetentionInputs Inputs(
        IReadOnlyList<Run>? runs = null,
        IReadOnlyList<Attempt>? attempts = null,
        IReadOnlyList<PipelineVersion>? versions = null,
        IReadOnlyDictionary<Id<Pipeline>, PipelineActivation>? activations = null,
        IReadOnlyList<StoredObject>? objects = null,
        IReadOnlyList<ClusterJob>? jobs = null,
        IReadOnlyList<ClusterSecret>? secrets = null,
        IReadOnlyDictionary<Id<Pipeline>, RetentionPolicy>? policies = null)
        => new(Now, new HashSet<Id<Pipeline>> { P, Q }, runs ?? [], attempts ?? [], versions ?? [],
            activations ?? new Dictionary<Id<Pipeline>, PipelineActivation>(), objects ?? [], jobs ?? [], secrets ?? [], Policies: policies);

    // ---- runs: older than 6 months AND beyond the latest 20 ----

    [Test]
    [Arguments(25, 200.0, 5)]   // 25 old runs: the 5 oldest go
    [Arguments(25, 100.0, 0)]   // 25 recent runs: none go (younger than 6 months)
    [Arguments(15, 400.0, 0)]   // 15 ancient runs: none go (within the latest 20)
    [Arguments(21, 184.0, 1)]
    [Arguments(21, 182.0, 0)]
    public async Task Runs_ExpireOnlyWhenOldAndBeyondTheLatest(int count, double ageDays, int expected)
    {
        var runs = Enumerable.Range(1, count).Select(n => Run(P, n, ageDays)).ToList();

        var (expired, kept) = RetentionPlanner.SelectExpiredRuns(runs, Now, 20, TimeSpan.FromDays(183));

        await Assert.That(expired.Count).IsEqualTo(expected);
        await Assert.That(kept).IsEqualTo(count - expected);
        await Assert.That(expired.Select(r => r.Number)).IsEquivalentTo(Enumerable.Range(1, expected));
    }

    [Test]
    public async Task Runs_RankIsPerPipeline_AndRunningRunsNeverExpire()
    {
        var runs = Enumerable.Range(1, 22).Select(n => Run(P, n, 300, n == 1 ? RunStatus.Running : RunStatus.Failed))
            .Concat(Enumerable.Range(1, 20).Select(n => Run(Q, n, 300)))
            .ToList();

        var (expired, _) = RetentionPlanner.SelectExpiredRuns(runs, Now, 20, TimeSpan.FromDays(183));

        await Assert.That(expired.Select(r => (r.PipelineId, r.Number))).IsEquivalentTo(new[] { (P, 2) });
    }

    [Test]
    public async Task PipelinePolicy_OverridesTheInstanceThresholds_ForThatPipelineOnly()
    {
        // P keeps 2 runs for 10 days by its definition; Q has no override (latest 20, 183 days).
        var runs = Enumerable.Range(1, 5).Select(n => Run(P, n, 30))
            .Concat(Enumerable.Range(1, 25).Select(n => Run(Q, n, 30)))
            .ToList();
        var policies = new Dictionary<Id<Pipeline>, RetentionPolicy> { [P] = new(2, TimeSpan.FromDays(10), TimeSpan.FromDays(1)) };

        var plan = RetentionPlanner.Plan(Inputs(runs, policies: policies), Options);

        await Assert.That(plan.ExpiredRuns.Select(r => (r.PipelineId, r.Number))).IsEquivalentTo(new[] { (P, 1), (P, 2), (P, 3) });
    }

    [Test]
    public async Task PipelinePolicy_BuildGrace_AppliesToThatPipelinesBinaries()
    {
        var policies = new Dictionary<Id<Pipeline>, RetentionPolicy> { [P] = new(20, TimeSpan.FromDays(183), TimeSpan.FromDays(1)) };
        var objects = new[]
        {
            Obj($"versions/{Hex(P)}/builds/aaa/step.tar.gz", 3),   // P: 1-day grace, unused: goes
            Obj($"versions/{Hex(Q)}/builds/bbb/step.tar.gz", 3),   // Q: 7-day grace: kept
        };

        var plan = RetentionPlanner.Plan(Inputs(objects: objects, policies: policies), Options);

        await Assert.That(plan.Objects.Where(o => o.Category == RetentionCategory.Build).Select(o => o.Object.Key))
            .IsEquivalentTo(new[] { $"versions/{Hex(P)}/builds/aaa/step.tar.gz" });
        await Assert.That(plan.BuildsKept).IsEqualTo(1);
    }

    [Test]
    public async Task Policies_ComeFromTheManifestOfTheVersionNewRunsStartOn()
    {
        var withRetention = Manifests.ManifestFixture.Node();
        withRetention["retention"] = new System.Text.Json.Nodes.JsonObject { ["keepLatestRuns"] = 3 };
        PipelineVersion Version(Id<Pipeline> p, string manifest) => new(Id.New<PipelineVersion>(), p, 1, manifest, "h", "sha",
            $"versions/{Hex(p)}/builds/x/step.tar.gz", new Dictionary<string, int>(), new Dictionary<string, int>(), Now);
        var pActive = Version(P, Manifests.ManifestFixture.Text);
        var pNext = Version(P, withRetention.ToJsonString());
        var qActive = Version(Q, Manifests.ManifestFixture.Text);
        var activations = new Dictionary<Id<Pipeline>, PipelineActivation>
        {
            [P] = new PipelineActivation.Draining(pActive.Id, 1, pNext.Id, 2, false, Now),
            [Q] = new PipelineActivation.Active(qActive.Id, 1, Now),
        };

        var policies = RetentionPolicies.Resolve(Options, [pActive, pNext, qActive], activations);

        await Assert.That(policies.Keys).IsEquivalentTo(new[] { P });
        await Assert.That(policies[P]).IsEqualTo(new RetentionPolicy(3, Options.MinRunAge, Options.BuildGrace));
    }

    [Test]
    public async Task PinnedRun_NeverExpires_AndKeepsItsObjects()
    {
        // The run whose publish-cli files /download serves (CliReleaseQuery) is old and beyond the latest 3.
        var runs = Enumerable.Range(1, 6).Select(n => Run(P, n, 300)).ToList();
        var pinned = runs[1];
        var objects = new[]
        {
            Obj($"runs/{Hex(P)}/1/publish-cli/1/files/pl-linux-x64", 300),
            Obj($"runs/{Hex(P)}/2/publish-cli/1/files/pl-linux-x64", 300),
        };

        var plan = RetentionPlanner.Plan(
            Inputs(runs, objects: objects) with { PinnedRuns = new HashSet<Id<Run>> { pinned.Id } },
            Options with { KeepLatestRuns = 3, MinRunAge = TimeSpan.FromDays(14) });

        await Assert.That(plan.ExpiredRuns.Select(r => r.Number)).IsEquivalentTo(new[] { 1, 3 });
        await Assert.That(plan.Objects.Select(o => o.Object.Key))
            .IsEquivalentTo(new[] { $"runs/{Hex(P)}/1/publish-cli/1/files/pl-linux-x64" });
    }

    [Test]
    public async Task ExpiredRun_ObjectsGo_ButNeverRunJson_RetainedRunsUntouched()
    {
        var runs = Enumerable.Range(1, 21).Select(n => Run(P, n, 300)).ToList();
        var objects = new[]
        {
            Obj($"runs/{Hex(P)}/1/run.json", 300),
            Obj($"runs/{Hex(P)}/1/build/1/output.json", 300),
            Obj($"runs/{Hex(P)}/1/build/1/files/image.tar", 300, 1000),
            Obj($"runs/{Hex(P)}/1/_failure/log-failure/abc/log", 300),
            Obj($"runs/{Hex(P)}/2/build/1/output.json", 300),
        };

        var plan = RetentionPlanner.Plan(Inputs(runs, objects: objects), Options);

        await Assert.That(plan.ExpiredRuns.Single().Number).IsEqualTo(1);
        await Assert.That(plan.Objects.Select(o => (o.Category, o.Object.Key))).IsEquivalentTo(new[]
        {
            (RetentionCategory.ExpiredRun, $"runs/{Hex(P)}/1/build/1/output.json"),
            (RetentionCategory.ExpiredRun, $"runs/{Hex(P)}/1/build/1/files/image.tar"),
            (RetentionCategory.ExpiredRun, $"runs/{Hex(P)}/1/_failure/log-failure/abc/log"),
        });
    }

    [Test]
    public async Task OrphanedRunObjects_GoAfterTheGrace_NeverRunJson()
    {
        var objects = new[]
        {
            Obj($"runs/{Hex(Q)}/7/run.json", 30),
            Obj($"runs/{Hex(Q)}/7/x/1/log", 30),
            Obj($"runs/{Hex(Q)}/8/x/1/log", 3), // too recent
        };

        var plan = RetentionPlanner.Plan(Inputs(objects: objects), Options);

        await Assert.That(plan.Objects.Select(o => (o.Category, o.Object.Key)))
            .IsEquivalentTo(new[] { (RetentionCategory.OrphanedRun, $"runs/{Hex(Q)}/7/x/1/log") });
    }

    [Test]
    public async Task ResultArchives_OfFinishedAttemptsGo_ActiveOnesStay()
    {
        var run = Run(P, 1, 1, RunStatus.Running);
        var done = new Attempt(Id.New<Attempt>(), P, run.Id, 1, "build", 1, new AttemptStatus.Failed(Now, Now), Now);
        var active = new Attempt(Id.New<Attempt>(), P, run.Id, 1, "test", 1, new AttemptStatus.InProgress(Now), Now);
        var objects = new[]
        {
            Obj($"runs/{Hex(P)}/1/build/1/result.tar.gz", 2),
            Obj($"runs/{Hex(P)}/1/test/1/result.tar.gz", 2),
            Obj($"runs/{Hex(P)}/1/gone/3/result.tar.gz", 2),
            Obj($"runs/{Hex(P)}/1/build/1/output.json", 2),
        };

        var plan = RetentionPlanner.Plan(Inputs([run], [done, active], objects: objects), Options);

        await Assert.That(plan.Objects.Select(o => o.Object.Key)).IsEquivalentTo(new[]
        {
            $"runs/{Hex(P)}/1/build/1/result.tar.gz",
            $"runs/{Hex(P)}/1/gone/3/result.tar.gz",
        });
    }

    // ---- binaries ----

    [Test]
    public async Task Binaries_KeptForActiveDrainingAndRetainedRuns_OthersGoAfterTheGrace()
    {
        PipelineVersion Version(int n, string key) => new(Id.New<PipelineVersion>(), P, n, "{}", "h", "sha", key,
            new Dictionary<string, int>(), new Dictionary<string, int>(), Now);
        var active = Version(5, $"versions/{Hex(P)}/builds/aaa/step.tar.gz");
        var drainFrom = Version(4, $"versions/{Hex(Q)}/builds/bbb/step.tar.gz");
        var usedByRun = Version(3, $"versions/{Hex(P)}/builds/ccc/step.tar.gz");
        var unused = Version(2, $"versions/{Hex(P)}/builds/ddd/step.tar.gz");
        var usedOnlyByExpired = Version(1, $"versions/{Hex(P)}/1/step.tar.gz");
        var runs = new List<Run> { Run(P, 30, 10, version: usedByRun.Id) };
        runs.AddRange(Enumerable.Range(1, 21).Select(n => Run(P, n, 300, version: n == 1 ? usedOnlyByExpired.Id : active.Id)));
        var activations = new Dictionary<Id<Pipeline>, PipelineActivation>
        {
            [P] = new PipelineActivation.Active(active.Id, 5, Now),
            [Q] = new PipelineActivation.Draining(drainFrom.Id, 4, Id.New<PipelineVersion>(), 6, false, Now),
        };
        var objects = new[]
        {
            Obj(active.BinaryKey, 30), Obj(drainFrom.BinaryKey, 30), Obj(usedByRun.BinaryKey, 30),
            Obj(unused.BinaryKey, 30), Obj(usedOnlyByExpired.BinaryKey, 300),
            Obj($"versions/{Hex(P)}/builds/eee/step.tar.gz", 3),   // a fresh build, no version: grace
            Obj($"versions/{Hex(P)}/builds/fff/step.tar.gz", 8),   // an old build, no version
            Obj($"versions/{Hex(P)}/5/manifest.json", 300),        // never touched
        };

        var plan = RetentionPlanner.Plan(
            Inputs(runs, versions: [active, drainFrom, usedByRun, unused, usedOnlyByExpired], activations: activations, objects: objects), Options);

        await Assert.That(plan.Objects.Where(o => o.Category == RetentionCategory.Build).Select(o => o.Object.Key)).IsEquivalentTo(new[]
        {
            unused.BinaryKey, usedOnlyByExpired.BinaryKey, $"versions/{Hex(P)}/builds/fff/step.tar.gz",
        });
        await Assert.That(plan.BuildsKept).IsEqualTo(4);
        await Assert.That(plan.Objects.Any(o => o.Object.Key.EndsWith("manifest.json"))).IsFalse();
    }

    [Test]
    public async Task StaleTriggerEvents_Go()
    {
        var objects = new[]
        {
            Obj($"triggers/{Hex(P)}/{Guid.NewGuid():N}/event.json", 8),
            Obj($"triggers/{Hex(P)}/{Guid.NewGuid():N}/event.json", 1),
        };

        var plan = RetentionPlanner.Plan(Inputs(objects: objects), Options);

        await Assert.That(plan.Objects.Count(o => o.Category == RetentionCategory.TriggerEvent)).IsEqualTo(1);
    }

    // ---- cluster ----

    private static ClusterJob Job(string name, string? role, Id<Pipeline>? pipeline, bool finished, double ageHours, int? ttl = 604800)
    {
        var labels = new Dictionary<string, string> { ["app"] = "olve-pipelines" };
        if (role is not null) labels["olve-role"] = role;
        if (pipeline is { } p) labels["olve-pipeline"] = Hex(p);
        return new ClusterJob(name, labels, Now - TimeSpan.FromHours(ageHours), finished, ttl);
    }

    [Test]
    public async Task UrlSecrets_GoOnlyWhenTheirJobIsGoneOrFinished_AndTheAttemptIsNotActive()
    {
        var run = Run(P, 1, 1, RunStatus.Running);
        var active = new Attempt(Id.New<Attempt>(), P, run.Id, 1, "a", 1, new AttemptStatus.Scheduled(), Now);
        var done = new Attempt(Id.New<Attempt>(), P, run.Id, 1, "b", 1, new AttemptStatus.Succeeded(Now, Now), Now);
        string Urls(Attempt a) => $"olve-urls-{a.Id.Value.Value:N}";
        var unknown = $"olve-urls-{Guid.NewGuid():N}";
        var secrets = new[]
        {
            new ClusterSecret(Urls(active), Now.AddHours(-5)),          // active attempt (job not yet created): keep
            new ClusterSecret(Urls(done), Now.AddHours(-5)),            // finished attempt, job gone: delete
            new ClusterSecret(unknown, Now.AddHours(-5)),               // unknown attempt, job finished: delete
            new ClusterSecret("olve-map-0123-urls", Now.AddHours(-5)),  // map-trigger job gone: delete
            new ClusterSecret("olve-fh-abc-urls", Now.AddHours(-5)),    // failure handler still running: keep
            new ClusterSecret("olve-build-x-y-urls", Now.AddMinutes(-5)), // too young: keep
            new ClusterSecret("olve-pipeline-123", Now.AddDays(-50)),   // a pipeline secret: never
            new ClusterSecret("something-urls", Now.AddDays(-50)),      // not ours: never
        };
        var jobs = new[]
        {
            Job("olve-" + unknown["olve-urls-".Length..], "step", P, finished: true, 5),
            Job("olve-fh-abc", "on-failure", P, finished: false, 5),
        };

        var plan = RetentionPlanner.Plan(Inputs([run], [active, done], jobs: jobs, secrets: secrets), Options);

        await Assert.That(plan.Secrets.Select(s => s.Name)).IsEquivalentTo(new[] { Urls(done), unknown, "olve-map-0123-urls" });
    }

    [Test]
    public async Task Jobs_OnlyV2RolesOfThisInstance_LeakedStepsAndStaleTtlLess()
    {
        var run = Run(P, 1, 1, RunStatus.Running);
        var active = new Attempt(Id.New<Attempt>(), P, run.Id, 1, "a", 1, new AttemptStatus.InProgress(Now), Now);
        var jobs = new[]
        {
            Job(active.KubernetesJobName, "step", P, finished: false, 5),            // active attempt: keep
            Job($"olve-{Guid.NewGuid():N}", "step", P, finished: false, 5),          // leaked step pod: delete
            Job($"olve-{Guid.NewGuid():N}", "step", P, finished: false, 0.5),        // too young: keep
            Job($"olve-{Guid.NewGuid():N}", null, null, finished: false, 500),       // v1 step Job: never
            Job("olve-fh-old", "failure-handler", P, finished: true, 500, ttl: null),// v1 handler role: never
            Job($"olve-{Guid.NewGuid():N}", "step", Id.New<Pipeline>(), false, 500), // another instance's pipeline: never
            Job("olve-fh-x", "on-failure", P, finished: true, 200, ttl: null),       // finished, no TTL, > 7 d: delete
            Job("olve-map-y", "map-trigger", P, finished: true, 200),                // has a TTL: K8s collects it
        };

        var plan = RetentionPlanner.Plan(Inputs([run], [active], jobs: jobs), Options);

        await Assert.That(plan.Jobs.Select(j => j.Name)).IsEquivalentTo(new[] { jobs[1].Name, "olve-fh-x" });
    }

    [Test]
    public async Task UnavailableListings_SkipTheirPart()
    {
        var inputs = Inputs(objects: null) with { Objects = null, Jobs = null, Secrets = [new ClusterSecret("olve-map-1-urls", Now.AddDays(-1))] };

        var plan = RetentionPlanner.Plan(inputs, Options);

        await Assert.That(plan.Objects).IsEmpty();
        await Assert.That(plan.Secrets).IsEmpty(); // no Job listing: cannot tell whether the Job is gone
    }

    [Test]
    public async Task Options_BindFromConfiguration()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:DryRun"] = "false",
                ["Retention:KeepLatestRuns"] = "5",
                ["Retention:MinRunAgeDays"] = "30",
                ["Retention:BuildGraceDays"] = "0.5",
            })
            .Build();

        var options = RetentionOptions.From(config);

        await Assert.That(options.DryRun).IsFalse();
        await Assert.That(options.KeepLatestRuns).IsEqualTo(5);
        await Assert.That(options.MinRunAge).IsEqualTo(TimeSpan.FromDays(30));
        await Assert.That(options.BuildGrace).IsEqualTo(TimeSpan.FromHours(12));
        await Assert.That(options.OrphanGrace).IsEqualTo(TimeSpan.FromDays(7));
        await Assert.That(RetentionOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).DryRun).IsTrue();
    }
}
