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

    // Catches: starting on the first answer without the second approval, or dropping the first answer.
    [Test]
    public async Task TwoInputs_WaitForBoth_AndKeepEachAnswer_WhichCantBeChanged()
    {
        var manifest = JsonNode.Parse(Manifest())!.AsObject();
        var inputs = ManifestFixture.Step(manifest, "deploy-beta")["externalInputs"]!.AsObject();
        inputs["audit"] = inputs["decision"]!.DeepClone();
        var s = new SchedulerScenario(manifest.ToJsonString());
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");

        var first = TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", "decision", Json("""{"approved":true}"""), "oliver"));

        await Assert.That(first.Status).IsTypeOf<Waiting>();
        await s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", "decision", Json("""{"approved":false}"""), "oliver").Problems.IsSingle<ConflictProblem>("Input 'decision' of step 'deploy-beta' is already answered.");

        var second = TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", "audit", Json("""{"approved":false,"comment":"no"}"""), "auditor"));

        await Assert.That(second.Status).IsTypeOf<Scheduled>();
        var input = JsonNode.Parse(second.Input!)!;
        await Assert.That(input["decision"]!["approved"]!.GetValue<bool>()).IsTrue();
        await Assert.That(input["audit"]!["comment"]!.GetValue<string>()).IsEqualTo("no");
        await Assert.That(second.Answers!.Keys).IsEquivalentTo(["decision", "audit"]);
    }

    [Test]
    public async Task InvalidValue_IsRejected_AndTheStepKeepsWaiting()
    {
        var (s, run) = AtTheGate();

        var result = s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":"yes"}"""), "oliver");

        await result.Problems.IsSingle<ValidationProblem>("value.approved: expected boolean, got string");
        await Assert.That(s.Latest(run, "deploy-beta")!.Status).IsTypeOf<Waiting>();
    }

    [Test]
    public async Task UnknownInputName_IsRejected()
    {
        var (s, run) = AtTheGate();

        await s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", "nope", Json("""{"approved":true}"""), "oliver").Problems.IsSingle<ValidationProblem>("name: Step 'deploy-beta' has no external input 'nope'.");
    }

    [Test]
    public async Task Refusals_NameWhatWasMissing()
    {
        var (s, run) = AtTheGate();
        var value = Json("""{"approved":true}""");

        await s.D.Inputs.Answer(s.Pipeline.Id, 99, "deploy-beta", null, value, "oliver").Problems.IsSingle<NotFoundProblem>("Run #99 not found.");
        await s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "gone", null, value, "oliver").Problems.IsSingle<NotFoundProblem>("Run #1 has no step 'gone'.");
    }

    [Test]
    public async Task SeveralInputs_MustBeNamed()
    {
        var manifest = JsonNode.Parse(Manifest())!.AsObject();
        var inputs = ManifestFixture.Step(manifest, "deploy-beta")["externalInputs"]!.AsObject();
        inputs["audit"] = inputs["decision"]!.DeepClone();
        var s = new SchedulerScenario(manifest.ToJsonString());
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        s.Succeed(run, "build");
        s.Succeed(run, "test");

        var answer = s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver");

        await answer.Problems.IsSingle<ValidationProblem>("name: Step 'deploy-beta' has several external inputs (decision, audit); name one.");
    }

    [Test]
    [Arguments(null, "declined by oliver")]
    [Arguments("  ", "declined by oliver")]
    [Arguments(" not today ", "declined by oliver: not today")]
    public async Task ADecline_RecordsWhoAndWhy(string? comment, string reason)
    {
        var (s, run) = AtTheGate();

        var declined = TestRunDomain.Pick(s.D.Inputs.Decline(s.Pipeline.Id, run.Number, "deploy-beta", comment, "oliver"));

        await Assert.That(((Failed)declined.Status).Reason).IsEqualTo(reason);
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

        await s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver").Problems.IsSingle<ConflictProblem>("Step 'deploy-beta' of run #1 is not waiting for input (it is scheduled).");
        await s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "build", null, Json("""{"approved":true}"""), "oliver").Problems.IsSingle<ConflictProblem>("Step 'build' has no external inputs.");
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

        await s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver").Problems.IsSingle<ConflictProblem>("Step 'deploy-beta' has no external input a person may answer.");
        await s.D.Inputs.Decline(s.Pipeline.Id, run.Number, "deploy-beta", "no", "oliver").Problems.IsSingle<ConflictProblem>("Step 'deploy-beta' has no external input a person may answer.");

        await s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", "decision", Json("""{"approved":true}"""), "oliver").Problems.IsSingle<ConflictProblem>("External input 'decision' of step 'deploy-beta' is not answered by a person (from: callback).");

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

    private const string ReviewUrl = "https://vr.example/reviews/7";

    /// <summary>The gate with a waiting link into <c>artifact.version</c>, whose build put <paramref name="version"/> there.</summary>
    private static (SchedulerScenario S, Run Run) AtALinkedGate(string version, string? label = "Review in VR")
    {
        var node = JsonNode.Parse(Manifest())!.AsObject();
        var link = new JsonObject { ["input"] = "artifact", ["path"] = "version" };
        if (label is not null) link["label"] = label;
        ManifestFixture.Step(node, "deploy-beta")["externalInputs"]!["decision"]!["link"] = link;
        var s = new SchedulerScenario(node.ToJsonString());
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        var output = JsonNode.Parse(DefaultOutput(run.Number, "build", 1)!)!;
        output["version"] = version;
        s.Succeed(run, "build", output.ToJsonString());
        s.Succeed(run, "test");
        return (s, run);
    }

    private static RunStepView ViewOf(SchedulerScenario s, Run run, string step)
        => TestRunDomain.Pick(s.D.Views.Get(s.Pipeline.Id, run.Number)).Stages.SelectMany(st => st.Steps).Single(x => x.Name == step);

    // Catches: a waiting step without the link its earlier step produced, in the run view or the step history.
    [Test]
    public async Task WaitingLink_IsResolvedFromTheInput_AndShownWhileTheStepWaits()
    {
        var (s, run) = AtALinkedGate(ReviewUrl);

        var waiting = s.Latest(run, "deploy-beta")!;
        await Assert.That(waiting.Status).IsTypeOf<Waiting>();
        await Assert.That(waiting.Links).IsEquivalentTo([new AttemptLink(ReviewUrl, "Review in VR")]);
        await Assert.That(ViewOf(s, run, "deploy-beta").Links!.Select(l => l.Url)).IsEquivalentTo([ReviewUrl]);
        await Assert.That(s.D.Views.History(s.Pipeline.Id, "deploy-beta", limit: 1).Single().Links).IsEquivalentTo(waiting.Links!);
    }

    [Test]
    public async Task WaitingLink_WithoutALabel_IsLabelledWithTheInputsTitle()
    {
        var (s, run) = AtALinkedGate(ReviewUrl, label: null);

        await Assert.That(s.Latest(run, "deploy-beta")!.Links).IsEquivalentTo([new AttemptLink(ReviewUrl, "Deploy to beta?")]);
    }

    // Catches: a value that isn't a URL failing the step (or linking to it) instead of only leaving the link out.
    [Test]
    public async Task WaitingLink_ThatIsNotAUrl_IsLeftOut_AndTheStepStillWaits()
    {
        var (s, run) = AtALinkedGate("1.2.3");

        var waiting = s.Latest(run, "deploy-beta")!;
        await Assert.That(waiting.Status).IsTypeOf<Waiting>();
        await Assert.That(waiting.Links).IsNull();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Running);
    }

    // Catches: a rerun's waiting attempt showing the earlier succeeded attempt's links instead of its own.
    [Test]
    public async Task RerunThatWaits_GetsItsLink_AndTheViewShowsItOverTheSucceededAttempts()
    {
        var (s, run) = AtALinkedGate(ReviewUrl);
        TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver"));
        var first = s.Succeed(run, "deploy-beta");
        await Assert.That(s.D.AttemptService.Update(first.Id, a => a with { Links = [new AttemptLink("https://beta.example", "App")] })).Succeeded();
        s.Succeed(run, "summary");

        var rerun = TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "deploy-beta", continueAfter: false));

        await Assert.That(rerun.Status).IsTypeOf<Waiting>();
        await Assert.That(rerun.Links).IsEquivalentTo([new AttemptLink(ReviewUrl, "Review in VR")]);
        await Assert.That(ViewOf(s, run, "deploy-beta").Links!.Select(l => l.Url)).IsEquivalentTo([ReviewUrl]);
    }

    /// <summary>The linked gate (<see cref="AtALinkedGate"/>) whose step runs a preflight before it waits.</summary>
    private static (SchedulerScenario S, Run Run) AtAPreflightGate(JsonArray? from = null)
    {
        var node = JsonNode.Parse(PreflightManifest(from))!.AsObject();
        var s = new SchedulerScenario(node.ToJsonString());
        var run = s.Start(Input());
        s.Succeed(run, "checkout");
        var output = JsonNode.Parse(DefaultOutput(run.Number, "build", 1)!)!;
        output["version"] = ReviewUrl;
        s.Succeed(run, "build", output.ToJsonString());
        s.Succeed(run, "test");
        return (s, run);
    }

    private static string PreflightManifest(JsonArray? from = null)
    {
        var node = JsonNode.Parse(Manifest(from))!.AsObject();
        var step = ManifestFixture.Step(node, "deploy-beta");
        step["externalInputs"]!["decision"]!["link"] = new JsonObject { ["input"] = "artifact", ["path"] = "version", ["label"] = "Review in VR" };
        step["preflight"] = true;
        return node.ToJsonString();
    }

    private static readonly IReadOnlyList<AttemptLink> PreflightLinks = [new(ReviewUrl, "Same review"), new("https://vr.example/runs/7", "Run")];

    // Catches: a preflight step waiting at once (no preflight), the API not showing it as running, or the preflight's links
    // replacing the waiting link instead of joining it.
    [Test]
    public async Task Preflight_RunsBeforeTheStepWaits_AndItsLinksJoinTheWaitingLink()
    {
        var (s, run) = AtAPreflightGate();
        var attempt = s.Latest(run, "deploy-beta")!;
        await Assert.That(attempt.InPreflight).IsTrue();
        await Assert.That(attempt.Status).IsTypeOf<Scheduled>();
        s.MarkInProgress(run, "deploy-beta");
        await Assert.That(ViewOf(s, run, "deploy-beta").Status).IsEqualTo(Olve.Pipelines.Api.RunStepStatus.InProgress);
        await Assert.That(ViewOf(s, run, "deploy-beta").Links!.Select(l => l.Url)).IsEquivalentTo([ReviewUrl]);

        var waiting = TestRunDomain.Pick(s.D.AttemptService.FinishPreflight(attempt.Id, PreflightLinks, ["decision"]));

        await Assert.That(waiting.Status).IsTypeOf<Waiting>();
        await Assert.That(waiting.InPreflight).IsFalse();
        await Assert.That(waiting.Links).IsEquivalentTo([new AttemptLink(ReviewUrl, "Review in VR"), new AttemptLink("https://vr.example/runs/7", "Run")]);
        await Assert.That(ViewOf(s, run, "deploy-beta").Status).IsEqualTo(Olve.Pipelines.Api.RunStepStatus.Waiting);
    }

    // Catches: the preflight's run time counted as time queued, or the input wait charged to the step on the critical path.
    [Test]
    public async Task APreflightStep_IsTimedApartFromItsWaitForInput()
    {
        var (s, run) = AtAPreflightGate();
        s.Clock.Advance(TimeSpan.FromSeconds(2));
        var attempt = s.MarkInProgress(run, "deploy-beta");
        s.Clock.Advance(TimeSpan.FromSeconds(20));
        TestRunDomain.Pick(s.D.AttemptService.FinishPreflight(attempt.Id, null, ["decision"]));
        s.Clock.Advance(TimeSpan.FromSeconds(60));
        TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver"));
        s.Clock.Advance(TimeSpan.FromSeconds(5));
        s.MarkInProgress(run, "deploy-beta");
        s.Clock.Advance(TimeSpan.FromSeconds(10));
        s.Succeed(run, "deploy-beta");
        s.Succeed(run, "summary");

        var phases = AttemptPhases.From(s.Latest(run, "deploy-beta")!)!;
        var stage = TestRunDomain.Pick(new CriticalPathService(s.D.RunService, s.D.AttemptService, s.D.Plans).Get(s.Pipeline.Id, null, null, includeUnsuccessful: false))
            .Runs.Single().Path.Single(p => p.Step == "deploy-beta");

        await Assert.That(Math.Round(phases.Queued)).IsEqualTo(7);
        await Assert.That(Math.Round(phases.Preflight!.Value)).IsEqualTo(20);
        await Assert.That(Math.Round(phases.Input)).IsEqualTo(60);
        await Assert.That(Math.Round(stage.Seconds)).IsEqualTo(37);
        await Assert.That(Math.Round(stage.WaitSeconds)).IsEqualTo(2);
    }

    // Catches: a callback that arrives before the preflight ends (a tool that answers at once) being refused or lost.
    [Test]
    public async Task AnAnswerDuringThePreflight_IsKept_AndRunsTheStepWhenThePreflightSucceeds()
    {
        var (s, run) = AtAPreflightGate();
        var attempt = s.MarkInProgress(run, "deploy-beta");

        var answered = TestRunDomain.Pick(s.D.Inputs.Answer(s.Pipeline.Id, run.Number, "deploy-beta", null, Json("""{"approved":true}"""), "oliver"));

        await Assert.That(answered.Status).IsTypeOf<InProgress>();
        await Assert.That(answered.Answers!["decision"].AnsweredBy).IsEqualTo("oliver");
        await Assert.That(TestRunDomain.Pick(s.D.AttemptService.FinishPreflight(attempt.Id, null, ["decision"])).Status).IsTypeOf<Scheduled>();
        s.Succeed(run, "deploy-beta");
        await Assert.That(JsonNode.Parse(s.Latest(run, "deploy-beta")!.Input!)!["decision"]!["approved"]!.GetValue<bool>()).IsTrue();
    }

    // Catches: the callback endpoint refusing a value that arrives during the preflight (the tool would have to retry).
    [Test]
    public async Task ACallbackDuringThePreflight_IsAccepted_AndADeclineIsAConflict()
    {
        var (s, run) = AtAPreflightGate(new JsonArray(new JsonObject { ["kind"] = "callback" }));
        s.MarkInProgress(run, "deploy-beta");
        var urls = new CallbackInputUrls(new CallbackKeyStore(new MemorySnapshotStore(), NullLogger<CallbackKeyStore>.Instance), new WebhookOptions("https://hooks.example/"));
        var url = (await urls.ForRunAsync(run.Id, TestRunDomain.Pick(s.D.Plans.ForRun(run)), CancellationToken.None))["deploy-beta/decision"];
        var handler = new CallbackInputHandler(urls, s.D.RunService, s.D.Inputs, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        Olve.Pipelines.Api.WebhookTriggersAnswerCallbackRequest Post(string body) =>
            new(run.Id.Value.Value, "deploy-beta", "decision", url[(url.LastIndexOf('/') + 1)..], new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));

        await Assert.That((await handler.HandleAsync(Post("""{"$decline":"no"}"""), CancellationToken.None)).Status).IsEqualTo(409);
        await Assert.That((await handler.HandleAsync(Post("""{"approved":true}"""), CancellationToken.None)).Status).IsEqualTo(202);

        var attempt = s.Latest(run, "deploy-beta")!;
        await Assert.That(attempt.Status).IsTypeOf<InProgress>();
        await Assert.That(attempt.Answers!["decision"].Source).IsEqualTo("callback");
    }

    [Test]
    public async Task ADeclineDuringThePreflight_IsAConflict()
    {
        var (s, run) = AtAPreflightGate();
        s.MarkInProgress(run, "deploy-beta");

        await s.D.Inputs.Decline(s.Pipeline.Id, run.Number, "deploy-beta", "no", "oliver").Problems
            .IsSingle<ConflictProblem>("Step 'deploy-beta' of run #1 is not waiting for input (it is running its preflight).");
    }

    // Catches: a failed preflight leaving the run waiting at the gate.
    [Test]
    public async Task AFailedPreflight_FailsTheStepAndTheRun_AndARerunRunsThePreflightAgain()
    {
        var (s, run) = AtAPreflightGate();

        s.Fail(run, "deploy-beta", "step 'deploy-beta' preflight failed: tool down");

        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Failed);
        var rerun = TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "deploy-beta", continueAfter: false));
        await Assert.That(rerun.Number).IsEqualTo(2);
        await Assert.That(rerun.InPreflight).IsTrue();
        await Assert.That(rerun.Status).IsTypeOf<Scheduled>();
    }

    // Catches: the older run's attempt, in its preflight when the newer run reached the gate, waiting beside the newer one.
    [Test]
    public async Task AnOlderAttemptThatEndsItsPreflightAfterANewerRunArrived_IsReplaced()
    {
        var (s, older) = AtAPreflightGate();
        var olderAttempt = s.MarkInProgress(older, "deploy-beta");
        var newer = s.Start(Input(sha: OtherSha));
        s.Succeed(newer, "checkout");
        s.Succeed(newer, "build");
        s.Succeed(newer, "test");
        await Assert.That(s.Latest(newer, "deploy-beta")!.InPreflight).IsTrue();
        await Assert.That(s.Latest(older, "deploy-beta")!.Status).IsTypeOf<InProgress>();

        TestRunDomain.Pick(s.D.AttemptService.FinishPreflight(olderAttempt.Id, null, ["decision"]));

        await Assert.That(s.Latest(older, "deploy-beta")!.Status).IsTypeOf<Obsolete>();
        await Assert.That(s.Get(older).Status).IsEqualTo(RunStatus.Superseded);
        await Assert.That(s.Latest(newer, "deploy-beta")!.InPreflight).IsTrue();
    }

    private static ExternalInput LinkedInput() => new("Approval", "Go?", "", [new ExternalSource.Person()]) { Link = new ExternalLink("review", "url", null) };

    public static IEnumerable<(string Input, string Problem)> UnresolvedLinks() =>
    [
        ("""{"other":{}}""", "Input 'review' has no value."),
        ("""{"review":null}""", "Input 'review' has no value."),
        ("""{"review":{"run":"7"}}""", "'review.url' is not in the input."),
        ("""{"review":"https://vr.example"}""", "'review.url' is not in the input."),
        ("""{"review":{"url":"/reviews/7"}}""", "'review.url' is not an absolute http(s) URL of at most 2048 characters."),
        ("""{"review":{"url":"ftp://vr.example"}}""", "'review.url' is not an absolute http(s) URL of at most 2048 characters."),
        ("""{"review":{"url":7}}""", "'review.url' is not an absolute http(s) URL of at most 2048 characters."),
        ("""{"review":{"url":"https://vr.example/""" + new string('a', 2048) + "\"}}", "'review.url' is not an absolute http(s) URL of at most 2048 characters."),
    ];

    [Test]
    [MethodDataSource(nameof(UnresolvedLinks))]
    public async Task WaitingLink_ThatDoesNotResolve_SaysWhy(string input, string problem)
    {
        await WaitingLinks.Resolve(LinkedInput(), Json(input)).Problems.IsSingle<ResultProblem>(problem);
    }

    private static IEnumerable<StepDescriptor> Steps(ManifestStage stage) => stage switch
    {
        StepsStage s => s.Steps,
        WhenStage w => w.Stages.SelectMany(Steps),
        _ => [],
    };
}
