using System.Text.Json;
using Olve.Results.TUnit;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Manifests;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>The self-deploy fixture with <c>deploy-beta</c> waiting for an <c>Approval</c> from a person.</summary>
public class ExternalInputTests
{
    private static string Manifest() => Mutated(root =>
    {
        root["models"]!["Approval"] = new JsonObject
        {
            ["output"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["approved"] = new JsonObject { ["type"] = "boolean" }, ["comment"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("approved"),
                ["additionalProperties"] = false,
            },
        };
        ManifestFixture.Step(root, "deploy-beta")["externalInputs"] = new JsonObject
        {
            ["decision"] = new JsonObject
            {
                ["model"] = "Approval",
                ["title"] = "Deploy to beta?",
                ["from"] = new JsonArray(new JsonObject { ["kind"] = "person" }),
            },
        };
    });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (SchedulerScenario S, Run Run) AtTheGate()
    {
        var s = new SchedulerScenario(Manifest());
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");
        return (s, run);
    }

    [Test]
    public async Task Gate_Waits_ThenTheAnswerSchedulesTheStepWithTheValue()
    {
        var (s, run) = AtTheGate();
        await Assert.That(s.Latest(run, "deploy-beta")!.Status).IsTypeOf<Waiting>();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);

        var answered = TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver"));

        await Assert.That(answered.Status).IsTypeOf<Scheduled>();
        var input = JsonNode.Parse(answered.Input!)!;
        await Assert.That(input["decision"]!["approved"]!.GetValue<bool>()).IsTrue();
        await Assert.That(input["artifact"]).IsNotNull();
        await Assert.That(answered.Answers!["decision"].AnsweredBy).IsEqualTo("oliver");

        s.Succeed(run, "deploy-beta");
        s.Succeed(run, "summary");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task InvalidValue_IsRejected_AndTheStepKeepsWaiting()
    {
        var (s, run) = AtTheGate();

        var result = s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":"yes"}"""), "oliver");

        await Assert.That(result).Failed();
        await Assert.That(s.Latest(run, "deploy-beta")!.Status).IsTypeOf<Waiting>();
    }

    [Test]
    public async Task UnknownInputName_IsRejected()
    {
        var (s, run) = AtTheGate();

        await Assert.That(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", "nope", Json("""{"approved":true}"""), "oliver")).Failed();
    }

    [Test]
    public async Task Decline_FailsTheStepWithoutRunningIt_AndTheRun()
    {
        var (s, run) = AtTheGate();

        var declined = TestRunDomain.Pick(s.D.Inputs.Decline(s.Pipeline.Id, run.Number, "deploy-beta", "not today", "oliver"));

        await Assert.That(declined.Status).IsTypeOf<Failed>();
        await Assert.That(((Failed)declined.Status).Reason).IsEqualTo("declined by oliver: not today");
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
    }

    [Test]
    public async Task AnsweringAStepThatIsNotWaiting_IsAConflict()
    {
        var (s, run) = AtTheGate();
        TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver"));

        await Assert.That(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver")).Failed();
        await Assert.That(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "build", null, Json("""{"approved":true}"""), "oliver")).Failed();
    }

    [Test]
    public async Task NewerRunReachingTheGate_ReplacesTheOlderWaitingOne()
    {
        var (s, older) = AtTheGate();
        var newer = s.Start(Input(sha: OtherSha));
        s.Succeed(newer, "checkout");
        s.Succeed(newer, "build");
        s.Succeed(newer, "test");

        await Assert.That(s.Latest(newer, "deploy-beta")!.Status).IsTypeOf<Waiting>();
        await Assert.That(s.Latest(older, "deploy-beta")!.Status).IsTypeOf<Obsolete>();
        await Assert.That(s.Get(older).Status).IsEqualTo(RunStatus.Superseded);
    }

    [Test]
    public async Task CancellingTheRun_CancelsTheWaitingStep()
    {
        var (s, run) = AtTheGate();

        TestRunDomain.Pick(s.D.Cancellations.Cancel(run.Id));

        await Assert.That(s.Latest(run, "deploy-beta")!.Status).IsTypeOf<Cancelled>();
    }

    [Test]
    public async Task ExternalInputs_SurviveAWriteAndParseRoundTrip_AndCountInTheStepVersion()
    {
        var manifest = ManifestFixture.ParseNode(JsonNode.Parse(Manifest())!);
        var reparsed = ManifestFixture.ParseNode(JsonNode.Parse(ManifestWriter.ToCanonicalJson(manifest))!);
        var step = reparsed.Stages.SelectMany(Steps).Single(x => x.Name == "deploy-beta");

        await Assert.That(step.ExternalInputs["decision"].Title).IsEqualTo("Deploy to beta?");
        await Assert.That(step.ExternalInputs["decision"].From.Single()).IsTypeOf<ExternalSource.Person>();

        var plain = ManifestFixture.Parse().Stages.SelectMany(Steps).Single(x => x.Name == "deploy-beta");
        var versions = new Dictionary<string, int>();
        await Assert.That(ManifestVersioning.StepContentHash(step, versions)).IsNotEqualTo(ManifestVersioning.StepContentHash(plain, versions));
    }

    private static IEnumerable<StepDescriptor> Steps(ManifestStage stage) => stage switch
    {
        StepsStage s => s.Steps,
        WhenStage w => w.Stages.SelectMany(Steps),
        _ => [],
    };
}
