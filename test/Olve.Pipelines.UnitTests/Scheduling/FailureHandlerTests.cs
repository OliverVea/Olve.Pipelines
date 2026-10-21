using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Failures;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>
/// onFailure end to end on the domain side, over the real <c>.pipelines-test</c> describe output
/// (<c>test-fixture.manifest.json</c>: hello | count, echo | when mode=normal: last; unscoped handler
/// <c>log-failure</c>): a run reaching Failed dispatches each handler in scope with a FailureContext that
/// validates against the pinned manifest's model.
/// </summary>
public class FailureHandlerTests
{
    private const string LinkBase = "https://pipelines.example";

    private sealed class CapturingLauncher : IFailureHandlerLauncher
    {
        public List<FailureHandlerDispatch> Launched { get; } = [];
        public void Launch(FailureHandlerDispatch dispatch) => Launched.Add(dispatch);
    }

    private sealed record Wired(SchedulerScenario S, CapturingLauncher Launcher);

    private static Wired Wire(string? manifest = null)
    {
        var s = new SchedulerScenario(manifest ?? TestFixtureManifest.Text);
        var launcher = new CapturingLauncher();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(s.D.Pipelines);
        services.AddSingleton(s.D.AttemptService);
        services.AddSingleton(s.D.VersionService);
        services.AddSingleton(s.D.Plans);
        services.AddSingleton(new FailureHandlerOptions(LinkBase));
        services.AddSingleton<IFailureHandlerLauncher>(launcher);
        services.AddTransient<FailureHandlerService>();
        var registration = new FailureHandlerEventRegistration(s.D.RunEvents, services.BuildServiceProvider());
        if (registration.Run().TryPickProblems(out var problems))
            throw new InvalidOperationException(string.Join("; ", problems));
        return new Wired(s, launcher);
    }

    private static string Input(string mode = "fail") => $$"""{"mode":"{{mode}}","sha":"{{SchedulerScenario.Sha}}"}""";

    private static string Greeting(int run) =>
        $$"""{"file":{{SchedulerScenario.FileRef(run, "hello", 1, "greeting.txt")}},"message":"hello"}""";

    /// <summary>hello succeeds; then echo fails (count as given). Returns the run.</summary>
    private static Run FailEcho(Wired w, bool countSucceeds = true)
    {
        var run = w.S.Start(Input());
        w.S.Succeed(run, "hello", Greeting(run.Number));
        if (countSucceeds) w.S.Succeed(run, "count", """{"lines":4}""");
        else w.S.Fail(run, "count", "count broke");
        w.S.Fail(run, "echo", "handler threw: echo: failing on purpose");
        return w.S.Get(run);
    }

    [Test]
    public async Task TheFixtureManifest_IsValid_AndDeclaresAnUnscopedHandler()
    {
        var manifest = TestFixtureManifest.Parse();
        var report = ManifestValidator.Validate(manifest);

        await Assert.That(report.Errors).IsEmpty();
        await Assert.That(manifest.FailureHandlers.Single().Step.Name).IsEqualTo("log-failure");
        await Assert.That(manifest.FailureHandlers.Single().Steps).IsNull();
    }

    [Test]
    public async Task FailedRun_DispatchesTheHandler_WithAValidFailureContext()
    {
        var w = Wire();
        var run = FailEcho(w);

        await Assert.That(run.Status).IsEqualTo(RunStatus.Failed);
        var dispatch = w.Launcher.Launched.Single();
        await Assert.That(dispatch.Handler.Name).IsEqualTo("log-failure");
        await Assert.That(dispatch.FailedSteps).IsEquivalentTo(["echo"]);
        await Assert.That(dispatch.JobName).Matches("^olve-fh-[0-9a-f]{24}$");
        await Assert.That(dispatch.InputKey).IsEqualTo(
            $"runs/{w.S.Pipeline.Id.Value.Value:N}/{run.Number}/_failure/log-failure/{dispatch.DispatchId}/input.json");

        var failure = JsonNode.Parse(dispatch.InputJson)!["failure"]!.AsObject();
        var pid = w.S.Pipeline.Id.Value.Value.ToString("D");
        await Assert.That(failure["pipeline"]!["id"]!.GetValue<string>()).IsEqualTo(pid);
        await Assert.That(failure["pipeline"]!["name"]!.GetValue<string>()).IsEqualTo("olve-pipelines");
        await Assert.That(failure["run"]!["number"]!.GetValue<int>()).IsEqualTo(run.Number);
        await Assert.That(failure["run"]!["address"]!.GetValue<string>()).IsEqualTo($"{pid}#{run.Number}");
        await Assert.That(failure["run"]!["versionStamp"]!.GetValue<string>()).IsEqualTo(run.VersionStamp);
        await Assert.That(failure["run"]!["trigger"]!.GetValue<string>()).IsEqualTo("manual");
        await Assert.That(failure["run"]!["url"]!.GetValue<string>()).IsEqualTo($"{LinkBase}/api/pipelines/{pid}/runs/{run.Number}");
        await Assert.That(failure["version"]!["number"]!.GetValue<int>()).IsEqualTo(w.S.Version.Number);
        await Assert.That(failure["version"]!["commit"]!.GetValue<string>()).IsEqualTo("deadbeef");
        await Assert.That(failure["input"]!["mode"]!.GetValue<string>()).IsEqualTo("fail");
        await Assert.That(failure["reason"]!.GetValue<string>()).IsEqualTo("step 'echo' failed: handler threw: echo: failing on purpose");
        var step = failure["failedSteps"]!.AsArray().Single()!;
        await Assert.That(step["step"]!.GetValue<string>()).IsEqualTo("echo");
        await Assert.That(step["attempt"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(step["address"]!.GetValue<string>()).IsEqualTo($"{pid}#{run.Number}/echo@1");
        await Assert.That(step["logUrl"]!.GetValue<string>()).IsEqualTo($"{LinkBase}/api/pipelines/{pid}/runs/{run.Number}/steps/echo/logs?attempt=1");

        // The harness validates input against the same model; so does the planner.
        var schema = TestFixtureManifest.Parse().Models["FailureContext"].Output;
        await Assert.That(JsonSchemaValidator.Validate(schema, failure)).Succeeded();
    }

    [Test]
    public async Task TwoFailedSteps_AreBothListed()
    {
        var w = Wire();
        FailEcho(w, countSucceeds: false);

        var failure = JsonNode.Parse(w.Launcher.Launched.Single().InputJson)!["failure"]!;
        await Assert.That(failure["failedSteps"]!.AsArray().Select(s => s!["step"]!.GetValue<string>()).ToArray())
            .IsEquivalentTo(["count", "echo"]);
        await Assert.That(failure["reason"]!.GetValue<string>()).StartsWith("2 steps failed: ");
    }

    [Test]
    public async Task SucceededAndCancelledRuns_DispatchNothing()
    {
        var w = Wire();
        var ok = w.S.Start(Input("skip-last"));
        w.S.Succeed(ok, "hello", Greeting(ok.Number));
        w.S.Succeed(ok, "count", """{"lines":4}""");
        w.S.Succeed(ok, "echo");
        await Assert.That(w.S.Get(ok).Status).IsEqualTo(RunStatus.Succeeded);

        var cancelled = w.S.Start(Input());
        TestRunDomain.Pick(w.S.D.RunService.Cancel(cancelled.Id));

        await Assert.That(w.Launcher.Launched).IsEmpty();
    }

    [Test]
    public async Task ScopedHandler_RunsOnlyForStepsInItsScope_AndListsOnlyThose()
    {
        var w = Wire(Mutated(m => m["failureHandlers"]![0]!["steps"] = new JsonArray("count")));

        FailEcho(w); // only echo failed
        await Assert.That(w.Launcher.Launched).IsEmpty();

        FailEcho(w, countSucceeds: false);
        var dispatch = w.Launcher.Launched.Single();
        await Assert.That(dispatch.FailedSteps).IsEquivalentTo(["count"]);
        var steps = JsonNode.Parse(dispatch.InputJson)!["failure"]!["failedSteps"]!.AsArray();
        await Assert.That(steps.Select(s => s!["step"]!.GetValue<string>()).ToArray()).IsEquivalentTo(["count"]);
    }

    [Test]
    public async Task ContinuedRunThatFailsAgain_DispatchesAgain_UnderANewJobName()
    {
        var w = Wire();
        var run = FailEcho(w);
        TestRunDomain.Pick(w.S.D.Continuations.ContinueRun(run.Id, Input()));
        w.S.Fail(run, "echo", "again");

        await Assert.That(w.Launcher.Launched).Count().IsEqualTo(2);
        await Assert.That(w.Launcher.Launched[0].JobName).IsNotEqualTo(w.Launcher.Launched[1].JobName);
        var failure = JsonNode.Parse(w.Launcher.Launched[1].InputJson)!["failure"]!;
        await Assert.That(failure["failedSteps"]![0]!["attempt"]!.GetValue<int>()).IsEqualTo(2);
    }

    [Test]
    public async Task OlderSdkFailureContext_IsSkipped_NotLaunched()
    {
        // The self-deploy fixture was described with the old flat FailureContext {pipeline, run, step, …}.
        var manifest = ManifestFixture.Parse();
        var d = new TestRunDomain();
        var pipeline = d.CreatePipeline();
        var version = d.AddVersion(pipeline.Id, ManifestFixture.Text);
        var run = d.CreateRun(version);
        var failed = d.Schedule("deploy-beta", run);
        await Assert.That(d.AttemptService.MarkFailed(failed.Id, "boom")).Succeeded();

        var (dispatches, skipped) = FailureHandlerPlanner.Plan(pipeline, run, version, manifest, [d.Get(failed.Id)], LinkBase);

        await Assert.That(dispatches).IsEmpty();
        await Assert.That(skipped.Single().Handler).IsEqualTo("aoe-triage");
        await Assert.That(skipped.Single().Reason).Contains("does not match this version's FailureContext model");
    }

    [Test]
    public async Task DispatchId_IsStablePerFailure()
    {
        var w = Wire();
        var run = FailEcho(w);

        await Assert.That(FailureHandlerPlanner.DispatchId(run, 0, "log-failure")).IsEqualTo(w.Launcher.Launched.Single().DispatchId);
        await Assert.That(FailureHandlerPlanner.DispatchId(run, 1, "log-failure")).IsNotEqualTo(w.Launcher.Launched.Single().DispatchId);
    }

    private static string Mutated(Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(TestFixtureManifest.Text)!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }
}
