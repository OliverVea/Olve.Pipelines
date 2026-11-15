using System.Text.Json.Nodes;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.UnitTests.Manifests;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>Reuse by identical tree: a step that succeeded on the same tree with the same code isn't run again.</summary>
public class CiReuseTests
{
    private const string A = "a111111111111111111111111111111111111111";
    private const string B = "b222222222222222222222222222222222222222";
    private const string C = "c333333333333333333333333333333333333333";

    private static void Code(JsonObject root, char digit = 'c') => root["codeHash"] = "sha256:" + new string(digit, 64);

    /// <summary>GitHub merges <paramref name="head"/> into the same tree as <paramref name="sameAs"/>.</summary>
    private static void SameTree(CiScenario s, string head, string sameAs) =>
        s.GitHub.Commits[CiScenario.Merge(head)] = new GitHubGitCommit(CiScenario.Merge(head),
            new GitHubShaRef("tree-" + CiScenario.Merge(sameAs)), [new GitHubShaRef(CiScenario.MainSha)]);

    private static async Task<CiRun> GreenRun(CiScenario s, int pull, string head)
    {
        s.Push(pull, head);
        await s.SettleAsync();
        var run = s.Run(pull);
        s.Succeed(run, "checkout", """{"tree":"t"}""");
        s.Succeed(run, "build");
        s.Succeed(run, "test");
        return s.Get(run);
    }

    private static async Task<CiRun> Start(CiScenario s, int pull, string head)
    {
        s.Push(pull, head);
        await s.SettleAsync();
        return s.Run(pull);
    }

    [Test]
    public async Task A_run_of_a_tree_that_passed_with_the_same_code_reuses_every_step_and_runs_no_Job()
    {
        await using var s = new CiScenario(r => Code(r));
        var first = await GreenRun(s, 7, A);
        SameTree(s, B, A);

        var second = s.Get(await Start(s, 8, B));

        await Assert.That(second.Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(s.Attempts.ListByRun(second.Id).Select(a => (a.StepName, a.Status is Succeeded, a.ReusedFrom!)))
            .IsEquivalentTo([
                ("checkout", true, new CiReuse(s.Latest(first, "checkout")!.Id, 1, "checkout", 1)),
                ("build", true, new CiReuse(s.Latest(first, "build")!.Id, 1, "build", 1)),
                ("test", true, new CiReuse(s.Latest(first, "test")!.Id, 1, "test", 1)),
            ]);
        // The input differs (it names the commit), the output is the original's.
        await Assert.That(s.Latest(second, "checkout")!.Input).IsNotEqualTo(s.Latest(first, "checkout")!.Input);
        await Assert.That(s.Latest(second, "checkout")!.Output!.Json).IsEqualTo("""{"tree":"t"}""");
    }

    public static IEnumerable<Func<(string Case, bool SameTree, char Code)>> NoReuse()
    {
        yield return () => ("another tree", false, 'c');
        yield return () => ("the same tree with other code", true, 'd');
    }

    [Test]
    [MethodDataSource(nameof(NoReuse))]
    public async Task A_step_is_run_again_for(string @case, bool sameTree, char code)
    {
        await using var s = new CiScenario(r => Code(r));
        await GreenRun(s, 7, A);
        if (sameTree)
            SameTree(s, B, A);
        if (code != 'c')
        {
            var other = TestRunDomain.Pick(s.Get<PipelineVersionService>().Add(s.Pipeline.Id, ManifestFixture.Edit(n =>
            {
                ManifestFixture.WithCi(n);
                Code(n, code);
            }), "sha256:" + Guid.NewGuid().ToString("N"), CiScenario.BuiltSha, new Dictionary<string, int>(), new Dictionary<string, int>()));
            TestRunDomain.Pick(s.Get<SwitchService>().Switch(s.Pipeline.Id, other.Id));
        }

        var second = await Start(s, 8, B);

        await Assert.That(s.Active(second)).IsEquivalentTo(["checkout"]);
        await Assert.That(s.Latest(second, "checkout")!.ReusedFrom).IsNull();
    }

    [Test]
    public async Task Running_a_pull_request_again_after_a_failure_runs_only_the_failed_step()
    {
        await using var s = new CiScenario(r => Code(r));
        var first = await Start(s, 7, A);
        s.Succeed(first, "checkout");
        s.Fail(first, "build");
        s.Succeed(first, "test");
        await Assert.That(s.Get(first).Status).IsEqualTo(RunStatus.Failed);

        TestRunDomain.Pick(await s.Get<CiRunStartService>().StartAsync(s.Pipeline.Id, 7));
        var again = s.Run(7);

        await Assert.That(again.Number).IsEqualTo(2);
        await Assert.That(s.Active(again)).IsEquivalentTo(["build"]);
        await Assert.That(s.Latest(again, "checkout")!.ReusedFrom!.Run).IsEqualTo(1);
        await Assert.That(s.Latest(again, "test")!.ReusedFrom!.Run).IsEqualTo(1);
        await Assert.That(s.Latest(again, "build")!.ReusedFrom).IsNull();

        await s.Get<CiStatusDelivery>().DrainAsync();
        var (_, status) = s.Statuses.Posted.Last();
        await Assert.That($"{status.State}: {status.Description}").IsEqualTo("pending: 2 of 3 steps done");
    }

    [Test]
    public async Task A_reuse_of_a_reused_step_points_at_the_attempt_that_ran()
    {
        await using var s = new CiScenario(r => Code(r));
        var first = await GreenRun(s, 7, A);
        SameTree(s, B, A);
        await Start(s, 8, B);
        SameTree(s, C, A);

        var third = await Start(s, 9, C);

        await Assert.That(s.Latest(third, "build")!.ReusedFrom).IsEqualTo(new CiReuse(s.Latest(first, "build")!.Id, 1, "build", 1));
    }

    [Test]
    public async Task Matrix_cells_are_reused_cell_by_cell()
    {
        await using var s = new CiScenario(r =>
        {
            Code(r);
            ManifestFixture.CiStep(r, "build")["matrix"] = ManifestFixture.Matrix(axes: ("os", ["linux", "windows"]));
        });
        var first = await Start(s, 7, A);
        s.Succeed(first, "checkout");
        s.Succeed(first, "build[os=linux]");
        s.Fail(first, "build[os=windows]");
        s.Succeed(first, "test");
        SameTree(s, B, A);

        var second = await Start(s, 8, B);

        await Assert.That(s.Active(second)).IsEquivalentTo(["build[os=windows]"]);
        await Assert.That(s.Latest(second, "build[os=linux]")!.ReusedFrom!.Step).IsEqualTo("build[os=linux]");
    }

    [Test]
    public async Task A_reused_step_reads_through_to_the_attempt_that_ran_and_says_so_when_it_is_gone()
    {
        await using var s = new CiScenario(r => Code(r));
        var first = await GreenRun(s, 7, A);
        SameTree(s, B, A);
        var second = await Start(s, 8, B);
        var id = s.Pipeline.Id.Value.Value;

        var view = s.Get<CiRunViewService>().Detail(s.Get(second));
        var output = await new GetCiStepOutputHandler(s.Get<CiAttemptResolver>())
            .HandleAsync(new Contract.CiRunStepsOutputRequest(id, second.Number, "checkout", null), default);

        await Assert.That(view.Stages[0].Steps[0].ReusedFrom!.Run).IsEqualTo(1);
        await Assert.That(view.Stages[0].Steps[0].Phases).IsNull();
        await Assert.That(((Contract.CiRunStepsOutputResponse.Ok)output).Body.Output!.Value.GetProperty("tree").GetString()).IsEqualTo("t");

        var reused = s.Latest(second, "checkout")!;
        await Assert.That(s.Attempts.Origin(reused)!.Id).IsEqualTo(s.Latest(first, "checkout")!.Id);

        s.Attempts.Delete(s.Latest(first, "checkout")!.Id).DiscardResult();

        await Assert.That(s.Attempts.Origin(reused)).IsNull();
        await Assert.That(CiAttemptResolver.OriginGone(reused).FormattedMessage)
            .IsEqualTo("Attempt 1 of step 'checkout' in CI run #2 reuses CI run #1/checkout@1, which no longer exists.");
    }
}
