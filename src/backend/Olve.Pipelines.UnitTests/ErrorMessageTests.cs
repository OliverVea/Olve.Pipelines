using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Harness;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests;

/// <summary>What a user reads when input is refused. Rewording a message means updating its row. Manifest issues have their own table.</summary>
public class ErrorMessageTests
{
    private sealed record Case(Type Problem, string Message, Func<ResultProblemCollection?> Problems);

    private static Case Invalid(string message, Func<ResultProblemCollection?> problems) => new(typeof(ValidationProblem), message, problems);

    private static Case Plain(string message, Func<ResultProblemCollection?> problems) => new(typeof(ResultProblem), message, problems);

    private static ResultProblemCollection? Schema(string schema, string value, string root = "input")
    {
        var deep = new JsonDocumentOptions { MaxDepth = 128 };
        using var doc = JsonDocument.Parse(schema, deep);
        return JsonSchemaValidator.Validate(doc.RootElement, JsonNode.Parse(value, null, deep), root).Problems;
    }

    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("""{"items":""", depth)) + "{}" + new string('}', depth);

    private static ResultProblemCollection? Yaml(string yaml) => YamlJson.Parse(yaml).Problems;

    private static ResultProblemCollection? Envelope(string json, int maxBytes = HarnessEnvelope.DefaultMaxBytes) => HarnessEnvelope.Parse(json, maxBytes).Problems;

    private static ResultProblemCollection? Build(string repo = "OliverVea/Olve.Pipelines", string sha = BuildJobBuilderTests.Sha, string path = ".pipelines") =>
        BuildJobBuilder.Build(BuildJobBuilderTests.Request(repo, sha, path), HarnessJobBuilderTests.Options).Problems;

    private static ResultProblemCollection? StepJob(HarnessJobRequest request) => HarnessJobBuilder.Build(request, HarnessJobBuilderTests.Options).Problems;

    private static ResultProblemCollection? MapTriggerJob(string trigger) => HarnessJobBuilder.BuildMapTrigger(
        new MapTriggerJobRequest("olve-map-abc", HarnessJobBuilderTests.PipelineId, trigger, "http://g/step?s", "http://g/event?s"),
        HarnessJobBuilderTests.Options).Problems;

    private static ResultProblemCollection? Domain(Func<TestRunDomain, ResultProblemCollection?> act) => act(new TestRunDomain());

    private static readonly ExternalAnswer Answer = new("true", "oliver", DateTimeOffset.UnixEpoch);

    private static readonly string BadJob = "Not_A_Job";

    private const string Sha64 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly Dictionary<string, Case> Cases = new()
    {
        // JSON Schema: config and run input
        ["schema nested too deep"] = Invalid(string.Concat(Enumerable.Repeat("[0]", 65).Prepend("input")) + ": schema nesting is too deep", () => Schema(Nested(66), string.Concat(Enumerable.Repeat("[", 66)) + new string(']', 66))),
        ["schema false"] = Invalid("input: no value is allowed here", () => Schema("false", "1")),
        ["schema not an object"] = Invalid("input: invalid schema (not an object)", () => Schema("1", "1")),
        ["unsupported keyword"] = Invalid("input: the input schema uses '$ref', which this service does not support", () => Schema("""{"$ref":"#/x"}""", "1")),
        ["wrong type: number"] = Invalid("input: expected string, got number", () => Schema("""{"type":"string"}""", "1")),
        ["wrong type: string"] = Invalid("input: expected number, got string", () => Schema("""{"type":"number"}""", "\"x\"")),
        ["wrong type: object"] = Invalid("input: expected string, got object", () => Schema("""{"type":"string"}""", "{}")),
        ["wrong type: array"] = Invalid("input: expected string, got array", () => Schema("""{"type":"string"}""", "[]")),
        ["wrong type: boolean"] = Invalid("input: expected string, got boolean", () => Schema("""{"type":"string"}""", "true")),
        ["wrong type: null"] = Invalid("input: expected string, got null", () => Schema("""{"type":"string"}""", "null")),
        ["wrong type: several allowed"] = Invalid("input: expected string or null, got number", () => Schema("""{"type":["string","null"]}""", "1")),
        ["fraction for an integer"] = Invalid("input: expected integer, got number", () => Schema("""{"type":"integer"}""", "3.5")),
        ["another root"] = Invalid("config: expected string, got number", () => Schema("""{"type":"string"}""", "1", "config")),
        ["const"] = Invalid("input: must be \"a\"", () => Schema("""{"const":"a"}""", "\"b\"")),
        ["const null"] = Invalid("input: must be null", () => Schema("""{"const":null}""", "\"b\"")),
        ["enum"] = Invalid("""input: must be one of ["a","b"]""", () => Schema("""{"enum":["a","b"]}""", "\"c\"")),
        ["anyOf not an array"] = Invalid("input: invalid schema ('anyOf' is not an array)", () => Schema("""{"anyOf":1}""", "1")),
        ["oneOf not an array"] = Invalid("input: invalid schema ('oneOf' is not an array)", () => Schema("""{"oneOf":1}""", "1")),
        ["union with siblings"] = Invalid("input: 'anyOf' combined with type, enum is not supported", () => Schema("""{"anyOf":[{}],"type":"string","enum":["a"]}""", "\"a\"")),
        ["oneOf matching two"] = Invalid("input: matches more than one of the allowed shapes", () => Schema("""{"oneOf":[{"type":"string"},{"type":"string","minLength":1}]}""", "\"x\"")),
        ["union matching none"] = Invalid("input: matches none of the allowed shapes (input: expected string, got number; input: expected null, got number)", () => Schema("""{"anyOf":[{"type":"string"},{"type":"null"}]}""", "5")),
        ["required"] = Invalid("input.a: is required", () => Schema("""{"type":"object","required":["a"]}""", "{}")),
        ["unknown property"] = Invalid("input.extra: is not allowed (unknown property)", () => Schema("""{"type":"object","additionalProperties":false}""", """{"extra":1}""")),
        ["record key"] = Invalid("input.Bad Key (key): must match ^[a-z.]+$", () => Schema("""{"type":"object","propertyNames":{"type":"string","pattern":"^[a-z.]+$"},"additionalProperties":{"type":"string"}}""", """{"Bad Key":"x"}""")),
        ["array item"] = Invalid("input[1]: must be at least 2 characters", () => Schema("""{"type":"array","items":{"type":"string","minLength":2}}""", """["ab","a"]""")),
        ["maxLength"] = Invalid("input: must be at most 1 characters", () => Schema("""{"type":"string","maxLength":1}""", "\"ab\"")),
        ["pattern"] = Invalid("input: must match ^a$", () => Schema("""{"type":"string","pattern":"^a$"}""", "\"b\"")),
        ["pattern not a regex"] = Invalid("input: the schema's pattern is not a supported regular expression", () => Schema("""{"type":"string","pattern":"("}""", "\"b\"")),
        ["minimum"] = Invalid("input: must be ≥ 1", () => Schema("""{"type":"number","minimum":1}""", "0")),
        ["maximum"] = Invalid("input: must be ≤ 1", () => Schema("""{"type":"number","maximum":1}""", "2")),
        ["exclusiveMinimum"] = Invalid("input: must be > 1", () => Schema("""{"type":"number","exclusiveMinimum":1}""", "1")),
        ["exclusiveMaximum"] = Invalid("input: must be < 1", () => Schema("""{"type":"number","exclusiveMaximum":1}""", "1")),

        // config YAML
        ["yaml too large"] = Invalid("yaml: the document is larger than 256 KB", () => Yaml(new string('#', 256 * 1024 + 1))),
        ["yaml two documents"] = Invalid("yaml: line 2: only one YAML document is allowed", () => Yaml("a: 1\n---\nb: 2\n")),
        ["yaml not a mapping"] = Invalid("yaml: the document must be a mapping of config entries (name: value)", () => Yaml("- 1\n- 2\n")),
        ["yaml too deep"] = Invalid("yaml: line 1: the document is nested too deeply", () => Yaml("a: " + new string('[', 200) + new string(']', 200) + "\n")),
        ["yaml anchor"] = Invalid("yaml: line 1: anchors and aliases are not supported", () => Yaml("a: &x 1\nb: *x\n")),
        ["yaml tag on a scalar"] = Invalid("yaml: line 1: tag '!custom' is not supported", () => Yaml("a: !custom x\n")),
        ["yaml tag on a sequence"] = Invalid("yaml: line 1: tag '!custom' is not supported", () => Yaml("a: !custom [x]\n")),
        ["yaml tag on a mapping"] = Invalid("yaml: line 1: tag '!custom' is not supported", () => Yaml("a: !custom {b: 1}\n")),
        ["yaml key not plain"] = Invalid("yaml: line 1: mapping keys must be plain strings", () => Yaml("[a]: 1\n")),
        ["yaml duplicate key"] = Invalid("yaml: line 3: duplicate key 'b'", () => Yaml("a: 1\nb: 2\nb: 3\n")),
        ["yaml infinity"] = Invalid("yaml: line 1: '.inf' cannot be represented in JSON", () => Yaml("a: .inf\n")),
        ["yaml invalid tagged int"] = Invalid("yaml: line 1: 'x' is not a valid int", () => Yaml("a: !!int x\n")),

        // what a step reports
        ["envelope too large"] = Plain("Invalid harness envelope: envelope exceeds 4 bytes", () => Envelope("{ }  ", 4)),
        ["envelope not an object"] = Plain("Invalid harness envelope: envelope is not a JSON object", () => Envelope("[]")),
        ["envelope without a status"] = Plain("Invalid harness envelope: envelope has no string 'status'", () => Envelope("{}")),
        ["envelope with a numeric status"] = Plain("Invalid harness envelope: envelope has no string 'status'", () => Envelope("""{"status":1}""")),
        ["envelope of an unknown status"] = Plain("Invalid harness envelope: unknown envelope status 'weird'", () => Envelope("""{"status":"weird"}""")),
        ["succeeded without output"] = Plain("Invalid harness envelope: succeeded envelope must have exactly 'status' and 'output'", () => Envelope("""{"status":"succeeded"}""")),
        ["succeeded with an extra key"] = Plain("Invalid harness envelope: succeeded envelope must have exactly 'status' and 'output'", () => Envelope("""{"status":"succeeded","output":1,"extra":2}""")),
        ["status twice"] = Plain("Invalid harness envelope: succeeded envelope must have exactly 'status' and 'output'", () => Envelope("""{"status":"succeeded","status":"succeeded","output":1}""")),
        ["output too large"] = Plain("Invalid harness envelope: output exceeds 65536 bytes", () => Envelope($$"""{"output":"{{new string('x', 70 * 1024)}}","status":"succeeded"}""", 1024 * 1024)),
        ["skipped without a reason"] = Plain("Invalid harness envelope: skipped envelope must have exactly 'status' and a string 'reason'", () => Envelope("""{"status":"skipped"}""")),
        ["skipped with a numeric reason"] = Plain("Invalid harness envelope: skipped envelope must have exactly 'status' and a string 'reason'", () => Envelope("""{"status":"skipped","reason":5}""")),
        ["failed without an error"] = Plain("Invalid harness envelope: failed envelope must have exactly 'status' and 'error'", () => Envelope("""{"status":"failed"}""")),
        ["failed with a string error"] = Plain("Invalid harness envelope: failed envelope 'error' must be {kind, message} strings", () => Envelope("""{"status":"failed","error":"x"}""")),
        ["failed of an unknown kind"] = Plain("Invalid harness envelope: unknown failure kind 'boom'", () => Envelope("""{"status":"failed","error":{"kind":"boom","message":"x"}}""")),

        // build and step Jobs
        ["build: repo with a space"] = Plain("Invalid GitHub repository 'not a repo'.", () => Build(repo: "not a repo")),
        ["build: repo with a command"] = Plain("Invalid GitHub repository 'a/b;rm -rf'.", () => Build(repo: "a/b;rm -rf")),
        ["build: branch for a sha"] = Plain("Invalid commit sha 'main' (want 40 lowercase hex).", () => Build(sha: "main")),
        ["build: path leaving the repo"] = Plain("Invalid pipeline path '../etc'.", () => Build(path: "../etc")),
        ["build: absolute path"] = Plain("Invalid pipeline path '/abs'.", () => Build(path: "/abs")),
        ["build: dot segment"] = Plain("Invalid pipeline path 'a/./b'.", () => Build(path: "a/./b")),
        ["build: job name"] = Plain($"Invalid build Job name '{BadJob}'.", () => BuildJobBuilder.Build(BuildJobBuilderTests.Request() with { JobName = BadJob }, HarnessJobBuilderTests.Options).Problems),
        ["step job: name"] = Plain($"Invalid K8s Job name '{BadJob}'.", () => StepJob(HarnessJobBuilderTests.Request() with { JobName = BadJob })),
        ["step job: step name"] = Plain("Invalid step name 'Bad Step'.", () => StepJob(HarnessJobBuilderTests.Request() with { StepName = "Bad Step" })),
        ["step job: no image"] = Plain("Step 'deploy-beta' has no image.", () => StepJob(HarnessJobBuilderTests.Request() with { StepImage = " " })),
        ["step job: lower-case secret"] = Plain("Step 'deploy-beta' declares an invalid secret name 'lower'.", () => StepJob(HarnessJobBuilderTests.Request(secrets: ["lower"]))),
        ["step job: reserved result url"] = Plain("Step 'deploy-beta' declares an invalid secret name 'OLVE_RESULT_URL'.", () => StepJob(HarnessJobBuilderTests.Request(secrets: ["OLVE_RESULT_URL"]))),
        ["step job: reserved tmpdir"] = Plain("Step 'deploy-beta' declares an invalid secret name 'TMPDIR'.", () => StepJob(HarnessJobBuilderTests.Request(secrets: ["TMPDIR"]))),
        ["step job: no url secret"] = Plain("Job 'olve-aaaaaaaabbbbccccddddeeeeeeeeeeee' has neither an attempt nor a URL Secret name.", () => StepJob(HarnessJobBuilderTests.Request() with { AttemptId = null })),
        ["step job: file urls too large"] = Plain("Step 'deploy-beta' has 400 input file refs; the URL map exceeds 102400 bytes.", () => StepJob(HarnessJobBuilderTests.Request(fileUrls: Enumerable.Range(0, 400).ToDictionary(i => $"1/build/1/f{i}", _ => "http://garage/" + new string('x', 400))))),

        ["map-trigger job: name"] = Plain($"Invalid K8s Job name '{BadJob}'.", () => HarnessJobBuilder.BuildMapTrigger(
            new MapTriggerJobRequest(BadJob, HarnessJobBuilderTests.PipelineId, "push", "http://g/step?s", "http://g/event?s"), HarnessJobBuilderTests.Options).Problems),
        ["map-trigger job: blank trigger"] = Plain("Invalid trigger name ' '.", () => MapTriggerJob(" ")),
        ["map-trigger job: trigger with a space"] = Plain("Invalid trigger name 'a b'.", () => MapTriggerJob("a b")),

        // domain input
        ["pipeline without a name"] = Plain("Pipeline name is required.", () => Domain(d => d.PipelineService.SetDefinition(Id.New<Pipeline>(), " ", null).Problems)),
        ["step input too large"] = Plain($"Input of step 'build' exceeds {AttemptService.MaxInputJsonBytes} bytes.", () => Domain(d =>
            d.AttemptService.Schedule(d.CreateRun().Id, "build", input: "\"" + new string('x', AttemptService.MaxInputJsonBytes) + "\"").Problems)),
        ["step input malformed"] = Plain("Input of step 'build' is not well-formed JSON.", () => Domain(d => d.AttemptService.Schedule(d.CreateRun().Id, "build", input: "{ nope").Problems)),
        ["answering a step that isn't waiting"] = new(typeof(ConflictProblem), "Step 'build' is not waiting for input (it is scheduled).", () => Domain(d => d.AttemptService.Answer(d.Schedule().Id, "x", Answer, ["x"]).Problems)),
        ["declining a step that isn't waiting"] = new(typeof(ConflictProblem), "Step 'build' is not waiting for input (it is scheduled).", () => Domain(d => d.AttemptService.Decline(d.Schedule().Id, "no").Problems)),
        ["an answer too large"] = Invalid($"value: The input of step 'build' would exceed {AttemptService.MaxInputJsonBytes} bytes.", () => Domain(d =>
            d.AttemptService.Answer(TestRunDomain.Pick(d.AttemptService.Schedule(d.CreateRun().Id, "build", awaitsInput: true)).Id, "x",
                Answer with { Value = "\"" + new string('x', AttemptService.MaxInputJsonBytes) + "\"" }, ["x"]).Problems)),

        // file refs
        ["file ref: negative run"] = Plain("File ref '-1/build/1/a' has an invalid run or attempt number.", () => HarnessFileRefs.Validate(new HarnessFileRef(-1, "build", 1, "a", Sha64)).Problems),
        ["file ref: attempt zero"] = Plain("File ref '1/build/0/a' has an invalid run or attempt number.", () => HarnessFileRefs.Validate(new HarnessFileRef(1, "build", 0, "a", Sha64)).Problems),
        ["file ref: step"] = Plain("File ref has an invalid step name '../x'.", () => HarnessFileRefs.Validate(new HarnessFileRef(1, "../x", 1, "a", Sha64)).Problems),
        ["file ref: path"] = Plain("File ref '1/build/1' has an invalid path.", () => HarnessFileRefs.Validate(new HarnessFileRef(1, "build", 1, "../a", Sha64)).Problems),
        ["file ref: sha"] = Plain("File ref '1/build/1/a' has an invalid sha256.", () => HarnessFileRefs.Validate(new HarnessFileRef(1, "build", 1, "a", "XYZ")).Problems),
    };

    public static IEnumerable<string> CaseNames() => Cases.Keys;

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task EachRefusal_HasItsExactMessage(string name)
    {
        var c = Cases[name];

        await c.Problems().IsSingle(c.Problem, c.Message);
    }
}
