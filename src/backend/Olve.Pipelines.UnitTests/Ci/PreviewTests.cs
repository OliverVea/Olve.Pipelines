using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Manifests;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>
/// A pull request's preview: deployed by a never-reused step of its CI runs, removed by a close run once it closes or
/// merges, with the definition and commit of the run that deployed it.
/// </summary>
public class PreviewTests
{
    private const string A = "a111111111111111111111111111111111111111";
    private const string B = "b222222222222222222222222222222222222222";
    private const string Url = "https://pr-7.widgets-preview.example";

    /// <summary>CI gains a stage deploying the preview (never reused) and an <c>onClose</c> stage removing it.</summary>
    private static void WithPreview(JsonObject root)
    {
        var deploy = ManifestFixture.Step(root, "test").DeepClone().AsObject();
        deploy["name"] = "deploy-preview";
        deploy["reuse"] = false;
        root["ci"]!["stages"]!.AsArray().Add(new JsonObject { ["steps"] = new JsonArray(deploy) });
        var remove = ManifestFixture.Step(root, "checkout").DeepClone().AsObject();
        remove["name"] = "remove-preview";
        root["ci"]!["onClose"] = new JsonObject { ["steps"] = new JsonArray(remove) };
    }

    private static async Task<(CiScenario S, CiRun Run)> Deployed(int pull = 7, string head = A)
    {
        var s = new CiScenario(WithPreview);
        s.Push(pull, head);
        await s.SettleAsync();
        var run = s.Run(pull);
        s.SucceedAll(run);
        var deploy = s.Latest(run, "deploy-preview")!;
        CiScenario.Ok(s.Attempts.Update(deploy.Id, a => a with { Links = [new AttemptLink(Url)] }));
        s.Succeed(run, "deploy-preview");
        return (s, s.Get(run));
    }

    private static Preview PreviewOf(CiScenario s, int pull = 7) =>
        s.Get<PreviewService>().TryGet(s.Pipeline.Id, pull, out var preview) ? preview : throw new InvalidOperationException("no preview");

    private static CiRun CloseRun(CiScenario s, int pull = 7) => s.Runs.ListCloseRuns(s.Pipeline.Id, pull).Last();

    /// <summary>The latest <c>olve/preview</c> status delivered (delivery sends only the latest state of a commit).</summary>
    private static async Task<string> PreviewStatus(CiScenario s)
    {
        await s.Get<CiStatusDelivery>().DrainAsync();
        var (_, status) = s.Statuses.Posted.Last(p => p.Status.Context == PreviewStatusReporter.Context);
        return $"{status.State}: {status.Description} ({status.TargetUrl ?? "no link"})";
    }

    [Test]
    public async Task A_preview_is_deployed_by_its_run_and_removed_by_a_close_run_when_the_pull_request_merges()
    {
        var (s, run) = await Deployed();
        await using var _ = s;

        await Assert.That(run.Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That((PreviewOf(s).State, PreviewOf(s).Url, PreviewOf(s).DeployedByNumber)).IsEqualTo((PreviewState.Live, Url, run.Number));
        await Assert.That(await PreviewStatus(s)).IsEqualTo($"success: Preview is live ({Url})");

        s.Close(7, A, merged: true);

        var close = CloseRun(s);
        await Assert.That((close.Kind, close.Status, close.VersionId, close.TestedSha)).IsEqualTo((CiRunKind.Close, RunStatus.Running, run.VersionId, run.TestedSha));
        await Assert.That(close.Input).IsEqualTo($$"""{"repo":"{{PullRequestFixtures.Repo}}","sha":"{{run.TestedSha}}","kind":"close","pull":7}""");
        await Assert.That(s.Active(close)).IsEquivalentTo(["remove-preview"]);
        await Assert.That(PreviewOf(s).State).IsEqualTo(PreviewState.Removing);
        var link = CiStatusPlanner.Link(s.Get<CiStatusOptions>(), s.Pipeline.Id, run.Number) ?? "no link";
        await Assert.That(await PreviewStatus(s)).IsEqualTo($"pending: Removing the preview ({link})");

        s.Succeed(close, "remove-preview");

        await Assert.That(s.Get(close).Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(PreviewOf(s).State).IsEqualTo(PreviewState.Removed);
        // The close run is not the pull request's latest run, and reports no olve/ci.
        await Assert.That(s.Run(7).Id).IsEqualTo(run.Id);
        await s.Get<CiStatusDelivery>().DrainAsync();
        await Assert.That(s.Statuses.Posted.Last(p => p.Status.Context == CiCommitStatus.CiContext).Status.State).IsEqualTo("success");
        await Assert.That(await PreviewStatus(s)).IsEqualTo("success: Preview removed (no link)");
    }

    [Test]
    public async Task A_failed_deploy_reports_an_error_not_a_failure()
    {
        await using var s = new CiScenario(WithPreview);
        s.Push(7, A);
        await s.SettleAsync();
        var run = s.Run(7);
        s.SucceedAll(run);
        s.Fail(run, "deploy-preview");

        await Assert.That(PreviewOf(s).State).IsEqualTo(PreviewState.Live);
        await Assert.That(await PreviewStatus(s)).StartsWith("error: Deploy failed, see olve/ci");
    }

    [Test]
    public async Task A_failed_close_run_is_tried_again_with_backoff_while_the_pull_request_stays_closed()
    {
        var (s, _) = await Deployed();
        await using var __ = s;
        s.Close(7, A, merged: false);
        s.Fail(CloseRun(s), "remove-preview");

        await Assert.That((PreviewOf(s).State, PreviewOf(s).Failures)).IsEqualTo((PreviewState.Failing, 1));
        await Assert.That(s.Get<PreviewWarnings>().For(s.Pipeline.Id))
            .IsEquivalentTo(["The preview of pull request #7 failed to be removed 1 time(s); it is tried again while the pull request stays closed."]);

        s.Get<PreviewLifecycle>().RetryDue();
        await Assert.That(s.Runs.ListCloseRuns(s.Pipeline.Id, 7).Count).IsEqualTo(1);

        s.Clock.Advance(PreviewLifecycle.RetryBackoff[0]);
        s.Get<PreviewLifecycle>().RetryDue();

        await Assert.That(s.Runs.ListCloseRuns(s.Pipeline.Id, 7).Count).IsEqualTo(2);
        s.Succeed(CloseRun(s), "remove-preview");
        await Assert.That((PreviewOf(s).State, PreviewOf(s).Failures)).IsEqualTo((PreviewState.Removed, 0));
        await Assert.That(s.Get<PreviewWarnings>().For(s.Pipeline.Id)).IsEmpty();
    }

    [Test]
    public async Task A_reopened_pull_request_deploys_again_once_the_running_close_run_ended()
    {
        var (s, _) = await Deployed();
        await using var __ = s;
        s.Close(7, A, merged: false);
        var close = CloseRun(s);

        s.Push(7, B);
        await s.SettleAsync();
        var again = s.Run(7);
        s.SucceedAll(again);

        await Assert.That(again.Number).IsGreaterThan(close.Number);
        await Assert.That(s.Latest(again, "deploy-preview")).IsNull();
        await Assert.That(s.Get(close).Status).IsEqualTo(RunStatus.Running);

        s.Succeed(close, "remove-preview");

        await Assert.That(s.Latest(again, "deploy-preview")!.Status).IsTypeOf<Scheduled>();
        await Assert.That((PreviewOf(s).State, PreviewOf(s).DeployedBy)).IsEqualTo((PreviewState.Deploying, again.Id));
    }

    // Catches: a close run counted as the pull request's run of its head, so reopening it never runs CI.
    [Test]
    public async Task A_pull_request_closed_mid_run_and_reopened_at_the_same_head_gets_a_new_run()
    {
        await using var s = new CiScenario(WithPreview);
        s.Push(7, A);
        await s.SettleAsync();
        var first = s.Run(7);
        s.SucceedAll(first);
        s.Close(7, A, merged: false);
        s.Succeed(CloseRun(s), "remove-preview");
        await Assert.That(s.Get(first).Status).IsEqualTo(RunStatus.Cancelled);

        s.Push(7, A);
        await s.SettleAsync();

        await Assert.That(s.Run(7).Number).IsGreaterThan(CloseRun(s).Number);
        await Assert.That(s.Run(7).Kind).IsEqualTo(CiRunKind.Pull);
    }

    [Test]
    public async Task A_restart_resumes_the_running_close_run_and_starts_no_other()
    {
        var (s, _) = await Deployed();
        await using var __ = s;
        s.Close(7, A, merged: true);

        s.Restart();

        await Assert.That(s.Runs.ListCloseRuns(s.Pipeline.Id, 7).Count).IsEqualTo(1);
        await Assert.That(s.Active(CloseRun(s))).IsEquivalentTo(["remove-preview"]);
    }

    [Test]
    public async Task A_pipeline_without_onClose_records_no_preview()
    {
        await using var s = new CiScenario(r =>
        {
            WithPreview(r);
            r["ci"]!.AsObject().Remove("onClose");
        });
        s.Push(7, A);
        await s.SettleAsync();
        s.SucceedAll(s.Run(7));

        await Assert.That(s.Latest(s.Run(7), "deploy-preview")).IsNotNull();
        await Assert.That(s.Get<PreviewService>().TryGet(s.Pipeline.Id, 7, out _)).IsFalse();
    }
}
