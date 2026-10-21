using Olve.Pipelines.Pipelines.Runs;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

public class RunServiceTests
{
    private static readonly DateTimeOffset Oct21 = new(2026, 10, 21, 23, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task Create_NumbersRunsMonotonicallyPerPipeline()
    {
        var d = new TestRunDomain();
        var v1 = d.AddVersion(d.CreatePipeline().Id);
        var v2 = d.AddVersion(d.CreatePipeline().Id);

        var a1 = d.CreateRun(v1);
        var a2 = d.CreateRun(v1);
        var b1 = d.CreateRun(v2);

        await Assert.That(a1.Number).IsEqualTo(1);
        await Assert.That(a2.Number).IsEqualTo(2);
        await Assert.That(b1.Number).IsEqualTo(1);
    }

    [Test]
    public async Task Create_PinsVersion_StoresInput_AndStartsRunning()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);

        var run = d.CreateRun(version, input: """{"sha":"f00"}""", trigger: new RunTrigger(RunTriggerKind.Push, "sha:f00"));

        await Assert.That(run.VersionId).IsEqualTo(version.Id);
        await Assert.That(run.VersionNumber).IsEqualTo(version.Number);
        await Assert.That(run.Input).IsEqualTo("""{"sha":"f00"}""");
        await Assert.That(run.Status).IsEqualTo(RunStatus.Running);
        await Assert.That(run.Trigger).IsEqualTo(new RunTrigger(RunTriggerKind.Push, "sha:f00"));
        await Assert.That(run.FinishedAt).IsNull();
    }

    [Test]
    public async Task Create_DerivesVersionStampFromUtcDateAndNumber()
    {
        // 23:30 UTC on Oct 21 is already Oct 22 in UTC+2 — the stamp must use the UTC date.
        var d = new TestRunDomain(new MonotonicTimeProvider(Oct21, TimeSpan.Zero));
        var version = d.AddVersion(d.CreatePipeline().Id);

        d.CreateRun(version);
        var run = d.CreateRun(version);

        await Assert.That(run.VersionStamp).IsEqualTo("2026.10.21.2");
    }

    [Test]
    public async Task Create_RejectsVersionOfAnotherPipeline()
    {
        var d = new TestRunDomain();
        var foreign = d.AddVersion(d.CreatePipeline().Id);
        var pipeline = d.CreatePipeline();

        var result = d.RunService.Create(pipeline.Id, foreign.Id, "{}", new RunTrigger(RunTriggerKind.Manual));

        await Assert.That(result).Failed();
    }

    [Test]
    [Arguments("")]
    [Arguments("{ not json")]
    public async Task Create_RejectsMalformedInput(string input)
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);

        var result = d.RunService.Create(version.PipelineId, version.Id, input, new RunTrigger(RunTriggerKind.Manual));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Create_RerunOf_LinksOriginal_AndRejectsUnknownRun()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);
        var original = d.CreateRun(version);

        var rerun = d.CreateRun(version, rerunOf: original.Id);
        var bad = d.RunService.Create(version.PipelineId, version.Id, "{}", new RunTrigger(RunTriggerKind.Manual), Id.New<Run>());

        await Assert.That(rerun.RerunOf).IsEqualTo(original.Id);
        await Assert.That(rerun.Number).IsEqualTo(2);
        await Assert.That(bad).Failed();
    }

    // Run numbers are addresses (#412, version stamps, storage keys): never reused, even after the
    // newest run is deleted.
    [Test]
    public async Task Create_AfterDeletingNewestRun_DoesNotReuseItsNumber()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);
        d.CreateRun(version);
        var newest = d.CreateRun(version);

        await Assert.That(d.RunService.Delete(newest.Id).Succeeded).IsTrue();
        var next = d.CreateRun(version);

        await Assert.That(next.Number).IsEqualTo(3);
    }

    [Test]
    public async Task Create_Concurrently_YieldsDistinctNumbers()
    {
        var d = new TestRunDomain(TimeProvider.System);
        var version = d.AddVersion(d.CreatePipeline().Id);

        var runs = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => d.CreateRun(version))));

        await Assert.That(runs.Select(r => r.Number).Distinct()).Count().IsEqualTo(50);
        await Assert.That(runs.Max(r => r.Number)).IsEqualTo(50);
    }

    [Test]
    public async Task PeekNextNumber_IsWhatCreateAllocates()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);

        await Assert.That(d.RunService.PeekNextNumber(version.PipelineId)).IsEqualTo(1);
        d.CreateRun(version);
        var peeked = d.RunService.PeekNextNumber(version.PipelineId);

        await Assert.That(peeked).IsEqualTo(2);
        await Assert.That(d.CreateRun(version).Number).IsEqualTo(peeked);
    }

    [Test]
    public async Task Lookups_ByNumber_Latest_AndVersionStamp()
    {
        var d = new TestRunDomain(new MonotonicTimeProvider(Oct21, TimeSpan.Zero));
        var version = d.AddVersion(d.CreatePipeline().Id);
        var first = d.CreateRun(version);
        var second = d.CreateRun(version);
        var pipelineId = version.PipelineId;

        await Assert.That(d.RunService.TryGetByNumber(pipelineId, 1, out var byNumber)).IsTrue();
        await Assert.That(byNumber!.Id).IsEqualTo(first.Id);
        await Assert.That(d.RunService.TryGetByNumber(pipelineId, 3, out _)).IsFalse();

        await Assert.That(d.RunService.TryGetLatest(pipelineId, out var latest)).IsTrue();
        await Assert.That(latest!.Id).IsEqualTo(second.Id);

        await Assert.That(d.RunService.TryGetByVersionStamp(pipelineId, "2026.10.21.2", out var byStamp)).IsTrue();
        await Assert.That(byStamp!.Id).IsEqualTo(second.Id);
        // Right number, wrong date: "version X" must match exactly.
        await Assert.That(d.RunService.TryGetByVersionStamp(pipelineId, "2026.10.20.2", out _)).IsFalse();
        await Assert.That(d.RunService.TryGetByVersionStamp(pipelineId, "garbage", out _)).IsFalse();

        await Assert.That(d.RunService.ListByPipeline(pipelineId).Select(r => r.Number)).IsEquivalentTo(new[] { 2, 1 });
        await Assert.That(d.RunService.ListByVersion(version)).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Complete_SetsTerminalStatusOnce()
    {
        var d = new TestRunDomain();
        var run = d.CreateRun();

        var completed = d.RunService.Complete(run.Id, RunStatus.Failed);
        var again = d.RunService.Complete(run.Id, RunStatus.Succeeded);

        await Assert.That(completed).Succeeded();
        await Assert.That(TestRunDomain.Pick(completed).Status).IsEqualTo(RunStatus.Failed);
        await Assert.That(TestRunDomain.Pick(completed).FinishedAt).IsNotNull();
        await Assert.That(again).Failed();
        await Assert.That(d.RunService.Complete(run.Id, RunStatus.Running)).Failed();
    }

    [Test]
    public async Task Supersede_LinksNewerRun()
    {
        var d = new TestRunDomain();
        var version = d.AddVersion(d.CreatePipeline().Id);
        var older = d.CreateRun(version);
        var newer = d.CreateRun(version);

        var result = d.RunService.Supersede(older.Id, newer.Id);

        await Assert.That(result).Succeeded();
        var after = TestRunDomain.Pick(result);
        await Assert.That(after.Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(after.SupersededBy).IsEqualTo(newer.Id);
        await Assert.That(d.RunService.Supersede(newer.Id, newer.Id)).Failed();
    }
}
