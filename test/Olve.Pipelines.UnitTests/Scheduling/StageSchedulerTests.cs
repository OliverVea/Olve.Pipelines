using Olve.Results.TUnit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Manifests;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>Stage barriers, input assembly, <c>when</c> skips, skip propagation and failure halts.</summary>
public class StageSchedulerTests
{
    [Test]
    public async Task HappyPath_Prod_RunsEveryStageInOrder_WithAssembledInputs()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "prod"));

        // Stage 1: checkout only, input mapped from the run input.
        await Assert.That(s.Active(run)).IsEquivalentTo(["checkout"]);
        var checkout = s.Latest(run, "checkout")!;
        await Assert.That(JsonNode.DeepEquals(Json(checkout.Input), Json($$"""{"repo":"OliverVea/Olve.Pipelines","sha":"{{Sha}}"}"""))).IsTrue();
        using (var doc = JsonDocument.Parse(checkout.Input!))
            await Assert.That(checkout.InputHash).IsEqualTo(CanonicalJson.Hash(doc.RootElement));

        var checkoutDone = s.Succeed(run, "checkout");

        // Stage 2: build + test in parallel, each given checkout's whole output.
        await Assert.That(s.Active(run)).IsEquivalentTo(["build", "test"]);
        await Assert.That(JsonNode.DeepEquals(Json(s.Latest(run, "build")!.Input), JsonNode.Parse($$"""{"source":{{checkoutDone.Output!.Json}}}"""))).IsTrue();

        // Barrier: build alone does not start deploy-beta.
        var build = s.Succeed(run, "build");
        await Assert.That(s.Active(run)).IsEquivalentTo(["test"]);
        s.Succeed(run, "test");

        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-beta"]);
        await Assert.That(JsonNode.DeepEquals(Json(s.Latest(run, "deploy-beta")!.Input), JsonNode.Parse($$"""{"artifact":{{build.Output!.Json}}}"""))).IsTrue();
        var beta = s.Succeed(run, "deploy-beta");

        await Assert.That(s.Active(run)).IsEquivalentTo(["deploy-prod"]);
        var prod = s.Succeed(run, "deploy-prod");

        await Assert.That(s.Active(run)).IsEquivalentTo(["publish-cli"]);
        await Assert.That(JsonNode.DeepEquals(Json(s.Latest(run, "publish-cli")!.Input),
            JsonNode.Parse($$"""{"artifact":{{build.Output!.Json}},"prod":{{prod.Output!.Json}}}"""))).IsTrue();
        var cli = s.Succeed(run, "publish-cli");

        await Assert.That(s.Active(run)).IsEquivalentTo(["summary"]);
        await Assert.That(JsonNode.DeepEquals(Json(s.Latest(run, "summary")!.Input),
            JsonNode.Parse($$"""{"beta":{{beta.Output!.Json}},"cli":{{cli.Output!.Json}},"prod":{{prod.Output!.Json}}}"""))).IsTrue();
        s.Succeed(run, "summary");

        var finished = s.Get(run);
        await Assert.That(finished.Status).IsEqualTo(RunStatus.Succeeded);
        await Assert.That(finished.FinishedAt).IsNotNull();
        await Assert.That(s.D.AttemptService.ListByRun(run.Id).All(a => a.Number == 1)).IsTrue();
    }

    [Test]
    public async Task Beta_SkipsProdBlock_WithWhenReason_AndLeavesOptionalInputsOut()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "beta"));

        s.SucceedThroughDeployBeta(run);

        foreach (var step in new[] { "deploy-prod", "publish-cli" })
        {
            var attempt = s.Latest(run, step)!;
            await Assert.That(IsSchedulerSkip(attempt, out var reason)).IsTrue();
            await Assert.That(reason).IsEqualTo("when deployTo ∉ [prod]");
            await Assert.That(attempt.Input).IsNull();
        }

        // summary runs with only its required input: optional prod/cli keys are absent, not null.
        var summary = s.Latest(run, "summary")!;
        await Assert.That(summary.Status).IsTypeOf<Scheduled>();
        var input = JsonNode.Parse(summary.Input!)!.AsObject();
        await Assert.That(input.Select(kv => kv.Key).ToList()).IsEquivalentTo(["beta"]);

        s.Succeed(run, "summary");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task NestedWhen_InnerConditionFails_SkipsOnlyInnerBlock()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "prod", publishCli: false));

        s.SucceedThroughDeployBeta(run);
        s.Succeed(run, "deploy-prod");

        await Assert.That(IsSchedulerSkip(s.Latest(run, "publish-cli")!, out var reason)).IsTrue();
        await Assert.That(reason).IsEqualTo("when publishCli ∉ [true]");
        var summaryInput = JsonNode.Parse(s.Latest(run, "summary")!.Input!)!.AsObject();
        await Assert.That(summaryInput.Select(kv => kv.Key).Order().ToList()).IsEquivalentTo(["beta", "prod"]);
    }

    [Test]
    public async Task SkipPropagation_RequiredInputFromSkippedStep_ChainsReasons()
    {
        // summary now *requires* prod and produces a TestReport; a new final step "notify" requires summary.
        var manifest = Mutated(root =>
        {
            var summary = ManifestFixture.Step(root, "summary");
            summary["inputTypes"]!["prod"] = ManifestFixture.ModelType("ProdDeployment", optional: false);
            summary["output"] = "TestReport";
            var notify = summary.DeepClone().AsObject();
            notify["name"] = "notify";
            notify["input"] = new JsonObject { ["report"] = ManifestFixture.Ref("summary") };
            notify["inputTypes"] = new JsonObject { ["report"] = ManifestFixture.ModelType("TestReport") };
            root["stages"]!.AsArray().Add(new JsonObject { ["steps"] = new JsonArray(notify) });
        });
        var s = new SchedulerScenario(manifest);
        var run = s.Start(Input(deployTo: "beta"));

        s.SucceedThroughDeployBeta(run);

        await Assert.That(IsSchedulerSkip(s.Latest(run, "summary")!, out var summaryReason)).IsTrue();
        await Assert.That(summaryReason).IsEqualTo("input 'prod' needs deploy-prod (skipped: when deployTo ∉ [prod])");
        await Assert.That(IsSchedulerSkip(s.Latest(run, "notify")!, out var notifyReason)).IsTrue();
        await Assert.That(notifyReason).IsEqualTo(
            "input 'report' needs summary (skipped: input 'prod' needs deploy-prod (skipped: when deployTo ∉ [prod]))");

        // Skips are not failures: the run succeeds.
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task HandlerSkip_PropagatesLikeSchedulerSkip()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input(deployTo: "prod"));
        s.SucceedThroughDeployBeta(run);

        var prod = s.Latest(run, "deploy-prod")!;
        TestRunDomain.Pick(s.D.AttemptService.MarkInProgress(prod.Id));
        await Assert.That(s.D.AttemptService.MarkSkipped(prod.Id, "nothing to deploy")).Succeeded();

        await Assert.That(IsSchedulerSkip(s.Latest(run, "publish-cli")!, out var reason)).IsTrue();
        await Assert.That(reason).IsEqualTo("input 'prod' needs deploy-prod (skipped: nothing to deploy)");
        await Assert.That(s.Active(run)).IsEquivalentTo(["summary"]);
    }

    [Test]
    public async Task Failure_HaltsAtBarrier_AfterSiblingsFinish()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");

        s.Fail(run, "build");

        // test is still running: the run waits at the barrier.
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);
        await Assert.That(s.Active(run)).IsEquivalentTo(["test"]);

        s.Succeed(run, "test");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
        await Assert.That(s.Latest(run, "deploy-beta")).IsNull();
        await Assert.That(s.Active(run)).IsEmpty();
    }

    [Test]
    public async Task CancelledAttempt_CancelsRun()
    {
        var s = new SchedulerScenario();
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "test");

        await Assert.That(s.D.AttemptService.Cancel(s.Latest(run, "build")!.Id)).Succeeded();

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Cancelled);
        await Assert.That(s.Latest(run, "deploy-beta")).IsNull();
    }

    [Test]
    public async Task UnparseableManifest_FailsRun()
    {
        var s = new SchedulerScenario();
        var bad = s.AddVersion("""{"apiVersion":"2.0"}""");
        TestRunDomain.Pick(s.D.Switches.Switch(s.Pipeline.Id, bad.Id, cancelRunning: true));

        var run = s.Start(Input());

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
    }

    [Test]
    public async Task OversizedInput_FailsStepWithoutRunning()
    {
        var manifest = Mutated(root =>
            ManifestFixture.Step(root, "deploy-beta")["input"]!["pad"] =
                ManifestFixture.Const(new string('x', AttemptService.MaxInputJsonBytes + 1)));
        var s = new SchedulerScenario(manifest);
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");

        var deploy = s.Latest(run, "deploy-beta")!;
        await Assert.That(deploy.Status).IsTypeOf<Failed>();
        await Assert.That(((Failed)deploy.Status).Reason).Contains("input.json exceeds");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
    }

    [Test]
    public async Task NoActiveVersion_StartFails()
    {
        var d = new TestRunDomain().WireEvents(scheduler: true);
        var pipeline = d.CreatePipeline();

        var result = d.Starts.Start(pipeline.Id, Input(), new RunTrigger(RunTriggerKind.Manual));

        await Assert.That(result).Failed();
    }
}
