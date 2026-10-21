using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.UnitTests.Harness;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Results;
using Olve.Results.TUnit;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.UnitTests.Scheduling;

public class JsonSchemaValidatorTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Result<JsonNode?> Validate(string schema, string value) => JsonSchemaValidator.Validate(Schema(schema), JsonNode.Parse(value));

    private static JsonElement FixtureInputView() => TestFixtureManifest.Parse().Models["TestInput"].Input!.Value;

    [Test]
    public async Task FixtureInput_FillsDefault()
    {
        var result = JsonSchemaValidator.Validate(FixtureInputView(), JsonNode.Parse("""{"sha":"abcdef1"}"""));

        await Assert.That(result).Succeeded();
        await Assert.That(result.Value!["mode"]!.GetValue<string>()).IsEqualTo("normal");
    }

    [Test]
    [Arguments("""{"mode":"normal"}""", "input.sha: is required")]
    [Arguments("""{"sha":"XYZ"}""", "input.sha: must match")]
    [Arguments("""{"sha":"abcdef1","mode":"fast"}""", "input.mode: must be one of")]
    [Arguments("""{"sha":12345678}""", "input.sha: expected string, got number")]
    public async Task FixtureInput_RejectsBadValues(string input, string expected)
    {
        var result = JsonSchemaValidator.Validate(FixtureInputView(), JsonNode.Parse(input));

        await Assert.That(result).Failed();
        result.TryPickProblems(out var problems);
        await Assert.That(string.Join("\n", problems!.Select(p => p.FormattedMessage))).Contains(expected);
    }

    [Test]
    public async Task OutputView_RejectsUnknownProperties()
    {
        var output = TestFixtureManifest.Parse().Models["TestInput"].Output;
        var result = JsonSchemaValidator.Validate(output, JsonNode.Parse("""{"sha":"abcdef1","mode":"normal","extra":1}"""));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Integers_Bounds_Arrays_AndNullableAnyOf()
    {
        const string schema = """
            {"type":"object","properties":{
              "n":{"type":"integer","exclusiveMinimum":0,"maximum":10},
              "tags":{"type":"array","items":{"type":"string","minLength":2}},
              "note":{"anyOf":[{"type":"string"},{"type":"null"}]}},
             "required":["n"],"additionalProperties":false}
            """;

        await Assert.That(Validate(schema, """{"n":3,"tags":["ab"],"note":null}""")).Succeeded();
        await Assert.That(Validate(schema, """{"n":3.5}""")).Failed();
        await Assert.That(Validate(schema, """{"n":0}""")).Failed();
        await Assert.That(Validate(schema, """{"n":11}""")).Failed();
        await Assert.That(Validate(schema, """{"n":1,"tags":["a"]}""")).Failed();
        await Assert.That(Validate(schema, """{"n":1,"note":5}""")).Failed();
    }

    [Test]
    public async Task UnsupportedKeyword_IsRejectedExplicitly()
    {
        var result = Validate("""{"type":"object","$ref":"#/$defs/x"}""", "{}");

        await Assert.That(result).Failed();
        result.TryPickProblems(out var problems);
        await Assert.That(problems!.Single().FormattedMessage).Contains("'$ref'");
    }

    [Test]
    public async Task Annotations_AreIgnored()
    {
        await Assert.That(Validate("""{"type":"string","format":"uri","description":"d","x-olve-kind":"fileRef"}""", "\"x\"")).Succeeded();
    }

    [Test]
    public async Task PollValuePath_ExtractsScalars()
    {
        var doc = JsonDocument.Parse("""{"sha":"abc","commit":{"n":3,"list":[{"id":"x"}]}}""").RootElement;

        await Assert.That(PollTriggerService.ExtractValue(doc, "sha").Value).IsEqualTo("abc");
        await Assert.That(PollTriggerService.ExtractValue(doc, "commit.n").Value).IsEqualTo("3");
        await Assert.That(PollTriggerService.ExtractValue(doc, "commit.list.0.id").Value).IsEqualTo("x");
        await Assert.That(PollTriggerService.ExtractValue(doc, "commit")).Failed();
        await Assert.That(PollTriggerService.ExtractValue(doc, "missing")).Failed();
    }
}

public class ManualTriggerTests
{
    private static (SchedulerScenario S, RunInputService Inputs) Build()
    {
        var s = new SchedulerScenario(TestFixtureManifest.Text);
        return (s, new RunInputService(s.D.ActivationService, s.D.VersionService, s.D.Plans, s.D.Starts));
    }

    [Test]
    public async Task ValidInput_StartsAManualRun_WithDefaultsApplied()
    {
        var (s, inputs) = Build();

        var outcome = inputs.StartManual(s.Pipeline.Id, JsonNode.Parse("""{"sha":"abcdef1"}"""));

        await Assert.That(outcome).Succeeded();
        var run = ((RunStartOutcome.Started)outcome.Value!).Run;
        await Assert.That(run.Trigger.Kind).IsEqualTo(RunTriggerKind.Manual);
        await Assert.That(JsonNode.Parse(run.Input)!["mode"]!.GetValue<string>()).IsEqualTo("normal");
        await Assert.That(s.Latest(run, "hello")!.Status).IsTypeOf<Scheduled>();
    }

    [Test]
    public async Task SkipLast_SkipsTheWhenBlock()
    {
        var (s, inputs) = Build();
        var run = ((RunStartOutcome.Started)inputs.StartManual(s.Pipeline.Id, JsonNode.Parse("""{"sha":"abcdef1","mode":"skip-last"}""")).Value!).Run;

        s.Succeed(run, "hello", """{"message":"m","file":{"run":1,"step":"hello","attempt":1,"path":"greeting.txt","sha256":"%"}}""".Replace("%", new string('a', 64)));
        s.Succeed(run, "count", """{"lines":3}""");
        s.Succeed(run, "echo");

        await Assert.That(s.Latest(run, "last")!.Status).IsTypeOf<Skipped>();
        await Assert.That(s.Get(run).Status).IsEqualTo(RunStatus.Succeeded);
    }

    [Test]
    public async Task InvalidInput_IsRejected_WithoutARun()
    {
        var (s, inputs) = Build();

        var outcome = inputs.StartManual(s.Pipeline.Id, JsonNode.Parse("""{"mode":"normal"}"""));

        await Assert.That(outcome).Failed();
        await Assert.That(s.D.RunService.ListByPipeline(s.Pipeline.Id)).IsEmpty();
    }

    [Test]
    public async Task MapTriggerJob_RunsTheMapperInHarnessBase()
    {
        var spec = HarnessJobBuilder.BuildMapTrigger(
            new MapTriggerJobRequest("olve-map-abc", HarnessJobBuilderTests.PipelineId, "push", "http://g/step?s", "http://g/event?s"),
            HarnessJobBuilderTests.Options);

        await Assert.That(spec).Succeeded();
        var pod = spec.Value!.Job.Spec.Template.Spec;
        await Assert.That(pod.Containers.Single().Image).IsEqualTo(HarnessJobBuilderTests.Options.HarnessBaseImage);
        await Assert.That(pod.Containers.Single().Command!.TakeLast(2)).IsEquivalentTo(new[] { "map-trigger", "push" });
        await Assert.That(spec.Value.UrlSecretData.Keys).IsEquivalentTo(new[] { HarnessJobBuilder.StepUrlKey, HarnessJobBuilder.InputUrlKey });
    }

    [Test]
    public async Task PushEvent_IsGitHubShaped()
    {
        var e = JsonNode.Parse(PushTriggerHandler.PushEvent("o/r", "v2", "abc"))!;

        await Assert.That(e["ref"]!.GetValue<string>()).IsEqualTo("refs/heads/v2");
        await Assert.That(e["after"]!.GetValue<string>()).IsEqualTo("abc");
        await Assert.That(e["repository"]!["full_name"]!.GetValue<string>()).IsEqualTo("o/r");
    }
}
