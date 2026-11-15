using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>A pull request that changes the pipeline's definition runs with its own: its tested commit is built on its own.</summary>
public class CiOwnDefinitionTests
{
    private const string A = "a000000000000000000000000000000000000000";
    private const string B = "b111111111111111111111111111111111111111";
    private static readonly string TestedA = CiScenario.Merge(A);

    private static string WithCi(Action<System.Text.Json.Nodes.JsonObject>? change = null) => ManifestFixture.Edit(node =>
    {
        ManifestFixture.WithCi(node);
        change?.Invoke(node);
    });

    /// <summary>A scenario whose pull request #7 changes the definition and is waiting for its build.</summary>
    private static async Task<(CiScenario S, CiRun Run)> Building(string[]? files = null)
    {
        var s = new CiScenario();
        s.GitHub.ChangedFiles[TestedA] = files ?? ["README.md", ".pipelines/ci.ts"];
        s.Push(7, A);
        await s.SettleAsync();
        await s.Builder.WhenStarted(TestedA);
        return (s, s.Run(7));
    }

    [Test]
    public async Task A_pull_request_that_changes_the_definition_waits_for_the_build_of_its_tested_commit()
    {
        var (s, run) = await Building();
        await using var _ = s;

        await Assert.That(run.Status).IsEqualTo(RunStatus.Running);
        await Assert.That(run.OwnDefinition).IsTrue();
        await Assert.That(run.Building).IsTrue();
        await Assert.That(s.Attempts.ListByRun(run.Id)).IsEmpty();
        await Assert.That(s.Builder.Started).IsEquivalentTo([TestedA]);
        var view = s.Get<CiRunViewService>().Detail(run);
        await Assert.That(view.Stages).IsEmpty();
        await Assert.That(view.DefinitionSha).IsEqualTo(TestedA);
    }

    [Test]
    public async Task GitHub_listing_only_part_of_the_changed_files_builds_the_pull_requests_own_definition_too()
    {
        var (s, run) = await Building([.. Enumerable.Range(0, 300).Select(i => $"src/file{i}.cs")]);
        await using var _ = s;

        await Assert.That(run.Building).IsTrue();
        await Assert.That(s.Builder.Started).IsEquivalentTo([TestedA]);
    }

    public static IEnumerable<Func<(string Name, Result<string> Build, RunStatus? Status, string[] Problems)>> Outcomes()
    {
        yield return () => ("valid with CI", WithCi(), RunStatus.Running, []);
        yield return () => ("build failed", new ResultProblem("Build/describe failed: the build failed (exit 1)."), RunStatus.Failed,
            ["Build/describe failed: the build failed (exit 1)."]);
        yield return () => ("invalid", WithCi(node => node["ci"]!["secrets"]!.AsArray().Add("NOPE")), RunStatus.Failed,
            ["[ci-secret-undeclared] ci.secrets[1]: ci.secrets lists 'NOPE', which is not in the pipeline's secrets"]);
        yield return () => ("CI removed", ManifestFixture.Edit(_ => { }), null, []);
    }

    [Test]
    [MethodDataSource(nameof(Outcomes))]
    public async Task The_build_decides_the_run(string name, Result<string> build, RunStatus? status, string[] problems)
    {
        var (s, run) = await Building();
        await using var _ = s;

        s.Builder.Finish(TestedA, build);
        await s.BuildsSettledAsync();

        await Assert.That(s.Get<PipelineVersionService>().ListByPipeline(s.Pipeline.Id)).HasSingleItem();
        if (status is null)
        {
            await Assert.That(s.Runs.ListByPull(s.Pipeline.Id, 7)).IsEmpty();
            return;
        }

        var after = s.Get(run);
        await Assert.That(after.Status).IsEqualTo(status.Value);
        await Assert.That(after.Building).IsFalse();
        await Assert.That(after.Problems ?? []).IsEquivalentTo(problems);
        if (status is RunStatus.Running)
            await Assert.That(s.Active(after)).IsEquivalentTo(["checkout"]);
        else
            await Assert.That(s.Attempts.ListByRun(run.Id)).IsEmpty();
    }

    [Test]
    public async Task A_run_with_its_own_definition_runs_its_build_s_binary()
    {
        var (s, run) = await Building();
        await using var _ = s;
        s.Builder.Finish(TestedA, WithCi());
        await s.BuildsSettledAsync();

        await Assert.That(s.Get<CiPlanCache>().BinaryKey(s.Get(run), out var key)).IsTrue();
        await Assert.That(key).IsEqualTo(Pipelines.StorageLayout.BuildBinaryKey(s.Pipeline.Id, TestedA));
    }

    [Test]
    public async Task A_new_push_during_the_build_supersedes_the_run_and_stops_its_build()
    {
        var (s, run) = await Building();
        await using var _ = s;
        s.GitHub.ChangedFiles[CiScenario.Merge(B)] = [".pipelines/ci.ts"];

        s.Push(7, B);
        await s.SettleAsync();
        await s.Builder.WhenStarted(CiScenario.Merge(B));

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Builder.Deleted).IsEquivalentTo([TestedA]);
        await Assert.That(s.Get<CiBuildService>().TryGet(s.Pipeline.Id, TestedA, out var forgotten)).IsFalse();
        await Assert.That(s.Run(7).Building).IsTrue();
        await Assert.That(s.Builder.Started).IsEquivalentTo([TestedA, CiScenario.Merge(B)]);
    }

    [Test]
    public async Task Running_the_same_commit_again_during_its_build_joins_the_build()
    {
        var (s, first) = await Building();
        await using var _ = s;

        await Assert.That(await s.Get<CiRunStartService>().StartAsync(s.Pipeline.Id, 7)).Succeeded();
        s.Builder.Finish(TestedA, WithCi());
        await s.BuildsSettledAsync();

        await Assert.That(s.Get(first).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Builder.Started).IsEquivalentTo([TestedA]);
        await Assert.That(s.Builder.Deleted).IsEmpty();
        await Assert.That(s.Active(s.Run(7))).IsEquivalentTo(["checkout"]);
    }

    [Test]
    public async Task A_second_run_of_a_built_commit_reuses_its_build()
    {
        var (s, _) = await Building();
        await using var __ = s;
        s.Builder.Finish(TestedA, WithCi());
        await s.BuildsSettledAsync();

        await Assert.That(await s.Get<CiRunStartService>().StartAsync(s.Pipeline.Id, 7)).Succeeded();
        await s.BuildsSettledAsync();

        await Assert.That(s.Builder.Started).IsEquivalentTo([TestedA]);
        await Assert.That(s.Active(s.Run(7))).IsEquivalentTo(["checkout"]);
    }

    [Test]
    public async Task Closing_the_pull_request_during_the_build_stops_it()
    {
        var (s, run) = await Building();
        await using var _ = s;

        s.Close(7, A, merged: false);

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Cancelled);
        await Assert.That(s.Builder.Deleted).IsEquivalentTo([TestedA]);
    }

    [Test]
    public async Task After_a_restart_the_build_a_running_run_waits_for_is_resumed()
    {
        await using var s = new CiScenario();
        var run = s.Runs.Create(new CiRunRequest(s.Pipeline.Id, 7, "t", "b", false, A, TestedA, "tree", CiScenario.MainSha, s.Version,
            "{\"repo\":\"r\",\"sha\":\"" + TestedA + "\"}", null, null, OwnDefinition: true));

        s.Get<CiDefinitionService>().ResumeBuilding();
        s.Builder.Finish(TestedA, WithCi());
        await s.BuildsSettledAsync();

        await Assert.That(s.Builder.Started).IsEquivalentTo([TestedA]);
        await Assert.That(s.Active(s.Get(run))).IsEquivalentTo(["checkout"]);
    }

    [Test]
    public async Task A_pull_request_that_adds_CI_to_a_pipeline_without_it_runs_its_own()
    {
        await using var s = new CiScenario(node => node.Remove("ci"));
        s.GitHub.ChangedFiles[TestedA] = [".pipelines/ci.ts"];
        s.Push(7, A);
        await s.SettleAsync();

        s.Builder.Finish(TestedA, WithCi());
        await s.BuildsSettledAsync();

        await Assert.That(s.Active(s.Run(7))).IsEquivalentTo(["checkout"]);
    }
}
