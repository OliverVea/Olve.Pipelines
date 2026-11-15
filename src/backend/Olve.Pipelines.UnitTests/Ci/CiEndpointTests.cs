using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared.Persistence;
using Olve.Utilities.Ids;
using Olve.Utilities.Stores;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Ci;

/// <summary>The CI operations as the API answers them, and CI runs surviving a restart.</summary>
public class CiEndpointTests
{
    private const string A = "a000000000000000000000000000000000000000";

    private static async Task<(CiScenario S, CiRun Run)> Running()
    {
        var s = new CiScenario();
        s.Push(7, A);
        await s.SettleAsync();
        return (s, s.Run(7));
    }

    [Test]
    public async Task A_run_shows_its_commits_and_each_steps_state()
    {
        var (s, run) = await Running();
        await using var _ = s;

        var response = await new GetCiRunHandler(s.Runs, s.Get<CiRunViewService>())
            .HandleAsync(new Contract.CiRunsGetRequest(s.Pipeline.Id.Value.Value, run.Number), default);

        var body = ((Contract.CiRunsGetResponse.Ok)response).Body;
        await Assert.That(body.Kind).IsEqualTo(Contract.CiRunKind.Pull);
        await Assert.That(body.Pulls).IsEquivalentTo([7]);
        await Assert.That(body.PullHeads.Single().HeadSha).IsEqualTo(A);
        await Assert.That(body.TestedSha).IsEqualTo(CiScenario.Merge(A));
        await Assert.That(body.DefinitionSha).IsEqualTo(CiScenario.BuiltSha);
        await Assert.That(body.Stages.SelectMany(st => st.Steps).Select(st => (st.Name, st.Status))).IsEquivalentTo([
            ("checkout", Contract.RunStepStatus.Scheduled), ("build", Contract.RunStepStatus.Pending), ("test", Contract.RunStepStatus.Pending),
        ]);
        await Assert.That(body.Steps.Select(st => st.Stage)).IsEquivalentTo([0, 1, 1]);
    }

    [Test]
    public async Task An_unknown_run_is_not_found()
    {
        await using var s = new CiScenario();

        var response = await new GetCiRunHandler(s.Runs, s.Get<CiRunViewService>())
            .HandleAsync(new Contract.CiRunsGetRequest(s.Pipeline.Id.Value.Value, 9), default);

        await Assert.That(((Contract.CiRunsGetResponse.NotFound)response).Body.Detail).IsEqualTo($"CI run #9 not found in pipeline '{s.Pipeline.Id}'.");
    }

    [Test]
    public async Task Cancelling_a_run_cancels_its_attempts_and_a_second_cancel_conflicts()
    {
        var (s, run) = await Running();
        await using var _ = s;
        var handler = new CancelCiRunHandler(s.Runs, s.Get<CiCancellationService>(), s.Get<CiRunViewService>(), s.Get<CiPersistenceService>());
        var request = new Contract.CiRunsCancelRequest(s.Pipeline.Id.Value.Value, run.Number);

        var first = await handler.HandleAsync(request, default);
        var second = await handler.HandleAsync(request, default);

        await Assert.That(((Contract.CiRunsCancelResponse.Ok)first).Body.Status).IsEqualTo(Contract.RunStatus.Cancelled);
        await Assert.That(s.Latest(run, "checkout")!.Status).IsTypeOf<AttemptStatus.Cancelled>();
        await Assert.That(((Contract.CiRunsCancelResponse.Conflict)second).Body.Detail).IsEqualTo("CI run #1 is already cancelled.");
    }

    [Test]
    public async Task Running_a_pull_request_again_supersedes_its_run_and_a_closed_one_conflicts()
    {
        var (s, run) = await Running();
        await using var _ = s;
        var handler = new RunPullHandler(s.Get<PipelineService>(), s.Get<CiRunStartService>(), s.Get<CiRunViewService>(), s.Get<CiPersistenceService>());

        var again = await handler.HandleAsync(new Contract.CiPullsRunRequest(s.Pipeline.Id.Value.Value, 7), default);
        s.Close(7, A, merged: false);
        var closed = await handler.HandleAsync(new Contract.CiPullsRunRequest(s.Pipeline.Id.Value.Value, 7), default);

        await Assert.That(((Contract.CiPullsRunResponse.Ok)again).Body.Number).IsEqualTo(2);
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(((Contract.CiPullsRunResponse.Conflict)closed).Body.Detail).IsEqualTo("Pull request #7 is closed.");
    }

    [Test]
    public async Task A_pull_request_lists_its_runs_newest_first()
    {
        var (s, _) = await Running();
        await using var __ = s;
        await new RunPullHandler(s.Get<PipelineService>(), s.Get<CiRunStartService>(), s.Get<CiRunViewService>(), s.Get<CiPersistenceService>())
            .HandleAsync(new Contract.CiPullsRunRequest(s.Pipeline.Id.Value.Value, 7), default);

        var response = await new GetPullHandler(s.Get<PipelineService>(), s.Get<PullRequestService>(), s.Get<CiRunViewService>())
            .HandleAsync(new Contract.CiPullsGetRequest(s.Pipeline.Id.Value.Value, 7), default);

        var body = (Contract.PullDetail)((Contract.CiPullsGetResponse.Ok)response).Body;
        await Assert.That(body.Runs.Select(r => (r.Number, r.Status))).IsEquivalentTo([(2, Contract.RunStatus.Running), (1, Contract.RunStatus.Superseded)],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(body.LatestRun!.Number).IsEqualTo(2);
    }

    [Test]
    public async Task A_steps_attempts_and_output_are_read_by_address()
    {
        var (s, run) = await Running();
        await using var _ = s;
        s.Succeed(run, "checkout", """{"tree":"x"}""");
        var id = s.Pipeline.Id.Value.Value;

        var attempts = await new ListCiRunAttemptsHandler(s.Runs, s.Attempts).HandleAsync(new Contract.CiRunsAttemptsRequest(id, run.Number), default);
        var output = await new GetCiStepOutputHandler(s.Get<CiAttemptResolver>()).HandleAsync(new Contract.CiRunStepsOutputRequest(id, run.Number, "checkout", null), default);
        var none = await new GetCiStepOutputHandler(s.Get<CiAttemptResolver>()).HandleAsync(new Contract.CiRunStepsOutputRequest(id, run.Number, "build", null), default);

        await Assert.That(((Contract.CiRunsAttemptsResponse.Ok)attempts).Body.Select(a => a.StepName)).IsEquivalentTo(["checkout", "build", "test"]);
        await Assert.That(((Contract.CiRunStepsOutputResponse.Ok)output).Body.Output!.Value.GetProperty("tree").GetString()).IsEqualTo("x");
        await Assert.That(((Contract.CiRunStepsOutputResponse.NotFound)none).Body.Detail).IsEqualTo("CI run #1 has no successful attempt of step 'build'.");
    }

    [Test]
    public async Task CI_runs_and_their_attempts_survive_a_restart_and_a_deleted_run_takes_its_file()
    {
        var snapshots = new MemorySnapshotStore();
        var (writer, runs, attempts) = Persistence(snapshots);
        await writer.StartingAsync(default);
        var pipelineId = Id.New<Pipeline>();
        var run = new CiRun(Id.New<CiRun>(), pipelineId, 4, 7, "Add a thing", "feature-7", false, A, "m1", "t1", "main1",
            Id.New<Pipelines.Versions.PipelineVersion>(), 2, """{"repo":"o/r","sha":"m1"}""", RunStatus.Running, DateTimeOffset.UnixEpoch);
        runs.Set(run);
        attempts.Set(new CiAttempt(Id.New<CiAttempt>(), pipelineId, run.Id, 4, "checkout", 1, new AttemptStatus.InProgress(DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch));
        await Assert.That((await writer.SavedAsync()).Succeeded).IsTrue();

        var (reader, readRuns, readAttempts) = Persistence(snapshots);
        await reader.StartingAsync(default);

        await Assert.That(readRuns.List().Single()).IsEqualTo(run);
        await Assert.That(readAttempts.List().Single().StepName).IsEqualTo("checkout");

        runs.Delete(run.Id).DiscardResult();
        (await writer.SavedAsync()).DiscardResult();
        await Assert.That(await snapshots.TryReadAsync(StorageLayout.CiRunStateKey(pipelineId, 4), default)).IsNull();
    }

    private static (CiPersistenceService, EntityStore<CiRun>, EntityStore<CiAttempt>) Persistence(ISnapshotStore snapshots)
    {
        var runs = new EntityStore<CiRun>([]);
        var attempts = new EntityStore<CiAttempt>([]);
        return (new CiPersistenceService(new EntityStore<PullRequest>([]), runs, attempts, new EntityStore<CiBuild>([]), new StorageOptions("bucket"), new PersistenceReadiness(),
            NullLogger<CiPersistenceService>.Instance, snapshots), runs, attempts);
    }
}
