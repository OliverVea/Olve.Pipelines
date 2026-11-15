using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Runs;

/// <summary>What a run changes since the newest older run that succeeded.</summary>
public class RunChangesTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class World
    {
        public TestRunDomain D { get; } = new();
        public FakeConfigSource Source { get; } = new();
        public PipelineConfigBindingService Bindings { get; }
        public Pipeline Pipeline { get; }
        public RunChangesService Service { get; }

        public Clock Clock { get; } = new();

        public World(bool bound = true)
        {
            Bindings = new PipelineConfigBindingService(new EntityStore<PipelineConfigBinding>([]), new IdProvider(), D.Time);
            Pipeline = D.CreatePipeline();
            if (bound)
                TestRunDomain.Pick(Bindings.Create(Pipeline.Id, "OliverVea/Olve.Pipelines", "main", ".pipelines", null));
            Service = new RunChangesService(D.PipelineService, D.RunService, Bindings, Source, new CommitComparisonCache(Clock), NullLogger<RunChangesService>.Instance);
        }

        private readonly Dictionary<int, PipelineVersion> _versions = [];

        public Run Run(RunStatus status, string? pushSha, int version = 1)
        {
            if (!_versions.TryGetValue(version, out var v))
                _versions[version] = v = D.AddVersion(Pipeline.Id, hash: $"sha256:{version}");
            var trigger = pushSha is null
                ? new RunTrigger(RunTriggerKind.Manual)
                : new RunTrigger(RunTriggerKind.Push, Origin: new RunOrigin(DateTimeOffset.UnixEpoch, "webhook", PushSha: pushSha));
            var run = D.CreateRun(v, trigger: trigger);
            if (pushSha is not null && Source.History.All(c => c.Sha != pushSha))
                Source.History.Add(new CommitInfo(pushSha, $"commit {pushSha}", "OliverVea", DateTimeOffset.UnixEpoch, $"https://github.com/c/{pushSha}"));
            return status switch
            {
                RunStatus.Running => run,
                RunStatus.Superseded => TestRunDomain.Pick(D.RunService.Supersede(run.Id, D.CreateRun().Id)),
                _ => TestRunDomain.Pick(D.RunService.Complete(run.Id, status)),
            };
        }

        public async Task<RunChanges> ChangesOf(Run run)
            => TestRunDomain.Pick(await Service.GetAsync(Pipeline.Id, run.Number, run.Number)).Single();
    }

    public static IEnumerable<Func<(string Rule, RunStatus[] Before, int? Since)>> Baselines() =>
    [
        () => ("the previous run succeeded", [RunStatus.Succeeded], 1),
        () => ("a superseded run in between doesn't count", [RunStatus.Succeeded, RunStatus.Superseded], 1),
        () => ("a failed run in between doesn't count", [RunStatus.Succeeded, RunStatus.Failed], 1),
        () => ("a cancelled run in between doesn't count", [RunStatus.Succeeded, RunStatus.Cancelled], 1),
        () => ("a running older run doesn't count", [RunStatus.Succeeded, RunStatus.Running], 1),
        () => ("the newest succeeded run wins", [RunStatus.Succeeded, RunStatus.Succeeded, RunStatus.Failed], 2),
        () => ("no older run succeeded", [RunStatus.Failed, RunStatus.Superseded], null),
        () => ("the first run", [], null),
    ];

    [Test]
    [MethodDataSource(nameof(Baselines))]
    public async Task ARunIsComparedWith_TheNewestOlderRunThatSucceeded(string rule, RunStatus[] before, int? since)
    {
        var w = new World();
        foreach (var status in before)
            w.Run(status, pushSha: null);
        var run = w.Run(RunStatus.Running, pushSha: null);

        var changes = await w.ChangesOf(run);

        await Assert.That(changes.Since?.Number).IsEqualTo(since);
    }

    [Test]
    public async Task ThePushedCommits_OfRunsThatDidntSucceed_CountForTheNextRun_NewestFirst()
    {
        var w = new World();
        w.Run(RunStatus.Succeeded, "aaa");
        w.Run(RunStatus.Superseded, "bbb");
        w.Run(RunStatus.Failed, "ccc");
        var run = w.Run(RunStatus.Succeeded, "ddd", version: 2);

        var changes = await w.ChangesOf(run);

        await Assert.That(changes.Since!.Number).IsEqualTo(1);
        await Assert.That(changes.Since.VersionNumber).IsEqualTo(1);
        await Assert.That(changes.Comparison!.Commits.Select(c => c.Sha)).IsEquivalentTo(new[] { "ddd", "ccc", "bbb" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(changes.Comparison.TotalCommits).IsEqualTo(3);
        await Assert.That(changes.CommitsError).IsNull();
    }

    [Test]
    public async Task WithoutAPushOnEitherSide_ThereAreNoCommits_ButTheRunToCompareWith()
    {
        var w = new World();
        w.Run(RunStatus.Succeeded, pushSha: null);
        var run = w.Run(RunStatus.Succeeded, "bbb");

        var changes = await w.ChangesOf(run);

        await Assert.That(changes.Since!.Number).IsEqualTo(1);
        await Assert.That(changes.Comparison).IsNull();
        await Assert.That(changes.CommitsError).IsNull();
        await Assert.That(w.Source.Comparisons).IsEqualTo(0);
    }

    public static IEnumerable<Func<(string Case, bool Bound, Result<CommitComparison>? Override, string Error)>> Failures() =>
    [
        () => ("unbound", false, null, "The pipeline isn't bound to a repository, so its commits can't be listed."),
        () => ("GitHub refused", true, new ResultProblem("GitHub returned 502 comparing aaa...bbb in 'OliverVea/Olve.Pipelines'."),
            "GitHub returned 502 comparing aaa...bbb in 'OliverVea/Olve.Pipelines'."),
    ];

    [Test]
    [MethodDataSource(nameof(Failures))]
    public async Task WhenTheCommitsCantBeListed_TheRunSaysWhy_AndTheRequestStillSucceeds(string @case, bool bound, Result<CommitComparison>? refusal, string error)
    {
        var w = new World(bound) { Source = { CompareOverride = refusal } };
        w.Run(RunStatus.Succeeded, "aaa");
        var run = w.Run(RunStatus.Succeeded, "bbb");

        var changes = await w.ChangesOf(run);

        await Assert.That(changes.Since!.Number).IsEqualTo(1);
        await Assert.That(changes.Comparison).IsNull();
        await Assert.That(changes.CommitsError).IsEqualTo(error);
    }

    [Test]
    public async Task AComparison_IsAskedForOnce_AndAFailureAgain_OnlyAfterAWhile()
    {
        var w = new World { Source = { CompareOverride = new ResultProblem("GitHub returned 403.") } };
        w.Run(RunStatus.Succeeded, "aaa");
        var run = w.Run(RunStatus.Succeeded, "bbb");

        await w.ChangesOf(run);
        w.Source.CompareOverride = null;
        var held = await w.ChangesOf(run);
        w.Clock.Now += CommitComparisonCache.FailureHold;
        await w.ChangesOf(run);
        await w.ChangesOf(run);

        await Assert.That(held.CommitsError).IsEqualTo("GitHub returned 403.");
        await Assert.That(w.Source.Comparisons).IsEqualTo(2);
    }

    [Test]
    public async Task TheDefaultRange_IsTheLatest20Runs_NewestFirst()
    {
        var w = new World();
        for (var i = 0; i < 22; i++)
            w.Run(RunStatus.Succeeded, pushSha: null);

        var changes = TestRunDomain.Pick(await w.Service.GetAsync(w.Pipeline.Id, null, null));

        await Assert.That(changes.Select(c => c.Number)).IsEquivalentTo(Enumerable.Range(3, 20).Reverse(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
