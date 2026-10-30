using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;
using static Olve.Pipelines.UnitTests.Scheduling.SchedulerScenario;

namespace Olve.Pipelines.UnitTests.Scheduling;

/// <summary>The self-deploy fixture with <c>deploy-beta</c> waiting for an <c>Approval</c> from a person.</summary>
public class ExternalInputTests
{
    private static string Manifest(JsonArray? from = null) => Mutated(root =>
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
                ["from"] = from ?? new JsonArray(new JsonObject { ["kind"] = "person" }),
            },
        };
    });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (SchedulerScenario S, Run Run) AtTheGate(JsonArray? from = null)
    {
        var s = new SchedulerScenario(Manifest(from));
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

    [Test]
    public async Task OnlyTheListedSources_MayAnswer()
    {
        var (s, run) = AtTheGate(new JsonArray(new JsonObject { ["kind"] = "callback" }));

        await Assert.That(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver")).Failed();
        await Assert.That(s.D.Inputs.Decline(s.Pipeline.Id, run.Number, "deploy-beta", "no", "oliver")).Failed();

        var answered = TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", "decision", Json("""{"approved":true}"""), new Answerer("callback", "callback from 10.0.0.1")));
        await Assert.That(answered.Status).IsTypeOf<Scheduled>();
        await Assert.That(answered.Answers!["decision"].Source).IsEqualTo("callback");
    }

    [Test]
    public async Task RunView_SaysWhatAWaitingStepWaitsFor()
    {
        var (s, run) = AtTheGate(new JsonArray(new JsonObject { ["kind"] = "callback" }, new JsonObject { ["kind"] = "person" }));

        var detail = TestRunDomain.Pick(s.D.Views.Get(s.Pipeline.Id, run.Number));
        var step = detail.Stages.SelectMany(x => x.Steps).Single(x => x.Name == "deploy-beta");

        await Assert.That(step.Status).IsEqualTo(Olve.Pipelines.Api.RunStepStatus.Waiting);
        await Assert.That(step.WaitingFor!).IsEquivalentTo(["callback", "person"]);
    }

    [Test]
    public async Task CallbackUrls_VerifyOnlyTheirOwnMac_AndSurviveARestart()
    {
        var (s, run) = AtTheGate(new JsonArray(new JsonObject { ["kind"] = "callback" }));
        var storage = new MemorySnapshotStore();
        CallbackInputUrls Urls() => new(new CallbackKeyStore(storage, NullLogger<CallbackKeyStore>.Instance), new WebhookOptions("https://hooks.example/"));
        var plan = TestRunDomain.Pick(s.D.Plans.ForRun(run));

        var urls = await Urls().ForRunAsync(run.Id, plan, CancellationToken.None);
        var url = urls["deploy-beta/decision"];
        await Assert.That(url).StartsWith($"https://hooks.example/api/hooks/inputs/{run.Id.Value.Value:N}/deploy-beta/decision/");
        var mac = url[(url.LastIndexOf('/') + 1)..];

        var restarted = Urls();
        await Assert.That(await restarted.VerifyAsync(run.Id, "deploy-beta", "decision", mac, CancellationToken.None)).IsTrue();
        await Assert.That(await restarted.VerifyAsync(run.Id, "deploy-beta", "other", mac, CancellationToken.None)).IsFalse();
        await Assert.That(await restarted.VerifyAsync(run.Id, "deploy-beta", "decision", new string('0', mac.Length), CancellationToken.None)).IsFalse();
        await Assert.That(await new CallbackInputUrls(new CallbackKeyStore(storage, NullLogger<CallbackKeyStore>.Instance), new WebhookOptions(null))
            .ForRunAsync(run.Id, plan, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task Poll_AnswersOnceTheValueValidates_OnlyFromAllowedPrivateHosts()
    {
        var body = """{"result":{"approved":true}}""";
        await using var server = LoopbackHttpServer.Start(_ => LoopbackHttpServer.Response(200, body));
        var from = new JsonArray(new JsonObject { ["kind"] = "poll", ["url"] = $"http://127.0.0.1:{server.Port}/status/{{run.number}}", ["valuePath"] = "result", ["intervalSeconds"] = 10 });
        var (s, run) = AtTheGate(from);

        ExternalInputPollService Poller(params string[] hosts) => new(s.D.AttemptService, s.D.RunService, s.D.Plans, s.D.Services,
            new ExternalInputPollOptions(hosts.ToHashSet(StringComparer.OrdinalIgnoreCase)), NullLogger<ExternalInputPollService>.Instance);
        async Task PollOnce(ExternalInputPollService poller)
        {
            foreach (var source in poller.Sources()) await source.CheckAsync(CancellationToken.None);
        }

        await PollOnce(Poller());
        await Assert.That(s.Latest(run, "deploy-beta")!.Status).IsTypeOf<Waiting>();
        await Assert.That(server.Accepted).IsEqualTo(0);

        body = """{"pending":true}""";
        await PollOnce(Poller("127.0.0.1"));
        await Assert.That(s.Latest(run, "deploy-beta")!.Status).IsTypeOf<Waiting>();
        await Assert.That(server.Requests.Single()).Contains($"GET /status/{run.Number} ");

        body = """{"result":{"approved":true}}""";
        await PollOnce(Poller("127.0.0.1"));
        var answered = s.Latest(run, "deploy-beta")!;
        await Assert.That(answered.Status).IsTypeOf<Scheduled>();
        await Assert.That(answered.Answers!["decision"].Source).IsEqualTo("poll");
    }

    [Test]
    public async Task PollAndCallbackSources_SurviveAWriteAndParseRoundTrip()
    {
        var from = new JsonArray(
            new JsonObject { ["kind"] = "callback" },
            new JsonObject { ["kind"] = "poll", ["url"] = "https://x.example/{run.id}", ["valuePath"] = "a.b", ["intervalSeconds"] = 15 });
        var manifest = ManifestFixture.ParseNode(JsonNode.Parse(Manifest(from))!);
        var reparsed = ManifestFixture.ParseNode(JsonNode.Parse(ManifestWriter.ToCanonicalJson(manifest))!);

        var sources = reparsed.Stages.SelectMany(Steps).Single(x => x.Name == "deploy-beta").ExternalInputs["decision"].From;
        await Assert.That(sources[0]).IsTypeOf<ExternalSource.Callback>();
        await Assert.That(sources[1]).IsEqualTo(new ExternalSource.Poll("https://x.example/{run.id}", "a.b", 15));
    }

    private static IEnumerable<StepDescriptor> Steps(ManifestStage stage) => stage switch
    {
        StepsStage s => s.Steps,
        WhenStage w => w.Stages.SelectMany(Steps),
        _ => [],
    };
}
