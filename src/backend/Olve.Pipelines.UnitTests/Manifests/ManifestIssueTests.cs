using System.Text;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.UnitTests.Configs;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Manifests.ManifestFixture;
using C = Olve.Pipelines.Pipelines.Manifests.ManifestIssueCodes;

namespace Olve.Pipelines.UnitTests.Manifests;

/// <summary>Every issue a manifest can get, with its exact path and message. Rewording a message means updating its row.</summary>
public class ManifestIssueTests
{
    private sealed record Case(string Code, string Path, string Message, Func<string> Json);

    private static Case Row(string code, string path, string message, Func<string> json) => new(code, path, message, json);

    private static Case Row(string code, string path, string message, Action<JsonObject> edit) => new(code, path, message, () => Edit(edit));

    // Fixture layout: stages[0] checkout · stages[1] build, test · stages[2] deploy-beta ·
    // stages[3] when{deployTo:prod} [deploy-prod, when{publishCli:true} [publish-cli]] · stages[4] summary;
    // failureHandlers[0] aoe-triage (deploy-prod, deploy-beta); triggers: push, poll.
    private static readonly Dictionary<string, Case> Cases = new()
    {
        // parse
        ["not json"] = Row(C.InvalidJson, "$", "not valid JSON (max depth 64): ", () => "{\"apiVersion\": "),
        ["trailing comma"] = Row(C.InvalidJson, "$", "not valid JSON (max depth 64): ", () => "{\"a\": 1,}"),
        ["too large"] = Row(C.TooLarge, "$", "manifest is 1048578 bytes; the limit is 1048576", () => "\"" + new string('x', ManifestParser.MaxBytes) + "\""),
        ["too deep"] = Row(C.InvalidJson, "$", "not valid JSON (max depth 64): ", () => new string('[', ManifestParser.MaxDepth + 1) + new string(']', ManifestParser.MaxDepth + 1)),
        ["duplicate key"] = Row(C.DuplicateKey, "apiVersion", "duplicate key 'apiVersion'", () => Text.Replace("\"apiVersion\": \"2.0\",", "\"apiVersion\": \"2.0\", \"apiVersion\": \"2.0\",")),
        ["lone surrogate"] = Row(C.InvalidValue, "name", "string is not valid Unicode (lone surrogate escape?)", () => Text.Replace("\"name\": \"Olve.Pipelines\"", "\"name\": \"\\ud800\"")),
        ["root not an object"] = Row(C.WrongType, "$", "expected an object, got an array", () => "[]"),
        ["missing stages"] = Row(C.MissingProperty, "stages", "missing required property 'stages'", () => Edit(r => r.Remove("stages"))),
        ["unknown property"] = Row(C.UnknownProperty, "foo", "unknown property 'foo'", () => Edit(r => r["foo"] = 1)),
        ["unknown step property"] = Row(C.UnknownProperty, "stages[1].steps[0].cpu", "unknown property 'cpu'", () => Edit(r => Step(r, "build")["cpu"] = 1)),
        ["unknown retention property"] = Row(C.UnknownProperty, "retention.keepForever", "unknown property 'keepForever'", () => Edit(r => r["retention"] = new JsonObject { ["keepForever"] = true })),
        ["retention field not an integer"] = Row(C.WrongType, "retention.minRunAgeDays", "expected an integer", () => Edit(r => r["retention"] = new JsonObject { ["minRunAgeDays"] = 1.5 })),
        ["name not a string"] = Row(C.WrongType, "name", "expected a string, got a number", () => Edit(r => r["name"] = 5)),
        ["api version"] = Row(C.UnsupportedApiVersion, "apiVersion", "apiVersion '3.0' is not supported (expected '2.0')", () => Edit(r => r["apiVersion"] = "3.0")),
        ["unknown trigger kind"] = Row(C.InvalidValue, "triggers[0].kind", "unknown trigger kind 'cron' (expected 'push', 'poll' or 'webhook')", () => Edit(r => r["triggers"]![0]!["kind"] = "cron")),
        ["push trigger renamed"] = Row(C.InvalidValue, "triggers[0].name", "a push trigger's name must be 'push'", () => Edit(r => r["triggers"]![0]!["name"] = "github")),
        ["poll without url"] = Row(C.MissingProperty, "triggers[1].url", "missing required property 'url'", () => Edit(r => r["triggers"]![1]!.AsObject().Remove("url"))),
        ["fractional interval"] = Row(C.WrongType, "triggers[1].intervalSeconds", "expected an integer", () => Edit(r => r["triggers"]![1]!["intervalSeconds"] = 1.5)),
        ["empty stage"] = Row(C.StageEmpty, "stages[0].steps", "a stage needs at least one step", () => Edit(r => r["stages"]![0]!["steps"] = new JsonArray())),
        ["stage of unknown shape"] = Row(C.MissingProperty, "stages[5]", "a stage is {steps}, {when, stages} or {select, discriminator, cases}", () => Edit(r => r["stages"]!.AsArray().Add(new JsonObject { ["parallel"] = 1 }))),
        ["when without stages"] = Row(C.MissingProperty, "stages[3].stages", "missing required property 'stages'", () => Edit(r => r["stages"]![3]!.AsObject().Remove("stages"))),
        ["when with no values"] = Row(C.InvalidValue, "stages[3].when.deployTo", "a when field needs at least one value", () => Edit(r => r["stages"]![3]!["when"]!["deployTo"] = new JsonArray())),
        ["when with object value"] = Row(C.WrongType, "stages[3].when.deployTo[0]", "a when value must be a string, number or boolean", () => Edit(r => r["stages"]![3]!["when"]!["deployTo"] = new JsonArray(new JsonObject()))),
        ["mapping not an object"] = Row(C.WrongType, "stages[0].steps[0].input.sha", "expected a mapping object ({ref}, {const} or nested), got a string", () => Edit(r => Step(r, "checkout")["input"]!["sha"] = "input.sha")),
        ["input type of unknown shape"] = Row(C.MissingProperty, "stages[1].steps[0].inputTypes.source", "an input type is either {model, optional} or {schema}", () => Edit(r => Step(r, "build")["inputTypes"]!["source"] = new JsonObject { ["type"] = "Source" })),
        ["output not a string"] = Row(C.WrongType, "stages[1].steps[0].output", "output must be a model name or null", () => Edit(r => Step(r, "build")["output"] = 1)),
        ["model schema not an object"] = Row(C.WrongType, "models.Source.output", "expected a JSON Schema object, got a boolean", () => Edit(r => r["models"]!["Source"]!["output"] = true)),
        ["failure handler steps not array"] = Row(C.WrongType, "failureHandlers[0].steps", "expected an array, got a string", () => Edit(r => r["failureHandlers"]![0]!["steps"] = "deploy-prod")),
        ["number out of range"] = Row(C.InvalidValue, "triggers[1].intervalSeconds", "number is out of range", () => Text.Replace("\"intervalSeconds\": 300", "\"intervalSeconds\": 1e400")),

        // availability
        ["same-stage sibling"] = Row(C.RefUnavailable, "stages[1].steps[1].input.source", "step 'test' input 'source': 'build' is not available at this stage (it runs in the same or a later stage)", r =>
        {
            Step(r, "test")["inputTypes"]!["source"] = ModelType("BuildArtifact");
            Step(r, "test")["input"]!["source"] = Ref("build");
        }
        ),
        ["later-stage ref"] = Row(C.RefUnavailable, "stages[0].steps[0].input.sha", "step 'checkout' input 'sha': 'build' is not available at this stage", r => Step(r, "checkout")["input"]!["sha"] = Ref("build.version")),
        ["unknown root"] = Row(C.RefUnavailable, "stages[2].steps[0].input.artifact", "step 'deploy-beta' input 'artifact': 'nope' is not available at this stage", r => Step(r, "deploy-beta")["input"]!["artifact"] = Ref("nope")),
        ["optional with no producer"] = Row(C.RefUnavailable, "stages[4].steps[0].input.cli", "step 'summary' input 'cli': 'publish-cli-x' is not available at this stage (optional() means maybe-skipped; there must still be an earlier producer)", r => Step(r, "summary")["input"]!["cli"] = Ref("publish-cli-x")),
        ["maybe-available as required model input"] = Row(C.RefMaybeAvailable, "stages[4].steps[0].input.prod", "step 'summary' input 'prod': ProdDeployment from 'deploy-prod' is only maybe-available (produced inside a when block); declare it optional(ProdDeployment)", r => Step(r, "summary")["inputTypes"]!["prod"]!["optional"] = false),
        ["maybe-available field into plain input"] = Row(C.RefMaybeAvailable, "stages[4].steps[0].input.url", "step 'summary' input 'url': 'deploy-prod.url' is only maybe-available (produced inside a when block); map it into an optional(...) input", r =>
        {
            Step(r, "summary")["inputTypes"]!["url"] = StringSchema();
            Step(r, "summary")["input"]!["url"] = Ref("deploy-prod.url");
        }
        ),
        ["ref to step without output"] = Row(C.RefNoOutput, "stages[4].steps[0].input.beta", "step 'summary' input 'beta': step 'deploy-beta' produces no output", r =>
        {
            Step(r, "deploy-beta")["output"] = null;
        }
        ),
        ["failure root outside handler"] = Row(C.RefFailureOutsideHandler, "stages[0].steps[0].input.sha", "step 'checkout' input 'sha': 'failure' is only available to failure handlers", r => Step(r, "checkout")["input"]!["sha"] = Ref("failure.step")),
        ["ref syntax"] = Row(C.RefSyntaxInvalid, "stages[0].steps[0].input.sha", "step 'checkout' input 'sha': 'input..sha' is not a valid ref ('<root>' or '<root>.<field>…')", r => Step(r, "checkout")["input"]!["sha"] = Ref("input..sha")),

        // external inputs
        ["external input of an unknown model"] = Row(C.ModelUnknown, "stages[2].steps[0].externalInputs.decision.model", "step 'deploy-beta' external input 'decision' references model 'Nope', which is not in models", r => Step(r, "deploy-beta")["externalInputs"] = External("Nope")),
        ["external input named like an input"] = Row(C.ExternalInputClash, "stages[2].steps[0].externalInputs.artifact", "step 'deploy-beta' external input 'artifact': 'artifact' is also a regular input", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", key: "artifact")),
        ["external input without a source"] = Row(C.ExternalInputNoSource, "stages[2].steps[0].externalInputs.decision.from", "step 'deploy-beta' external input 'decision': from must list at least one source", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", sources: [])),
        ["external input with an empty title"] = Row(C.ExternalInputTitleEmpty, "stages[2].steps[0].externalInputs.decision.title", "step 'deploy-beta' external input 'decision': title must not be empty", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", title: " ")),
        ["external input listing a source twice"] = Row(C.ExternalInputSource, "stages[2].steps[0].externalInputs.decision.from", "step 'deploy-beta' external input 'decision': each source kind may be listed once", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", sources: ["person", "person"])),
        ["callback input with a key that isn't URL-safe"] = Row(C.ExternalInputSource, "stages[2].steps[0].externalInputs.a.b", "step 'deploy-beta' external input 'a.b': a callback input's key must be letters, digits, '_' or '-' (it is part of the URL)", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", key: "a.b", sources: ["callback"])),
        ["external input listing a source twice beside another"] = Row(C.ExternalInputSource, "stages[2].steps[0].externalInputs.decision.from", "step 'deploy-beta' external input 'decision': each source kind may be listed once", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", sources: ["person", "person", "callback"])),
        ["callback input with an unsafe key beside another source"] = Row(C.ExternalInputSource, "stages[2].steps[0].externalInputs.a.b", "step 'deploy-beta' external input 'a.b': a callback input's key must be letters, digits, '_' or '-' (it is part of the URL)", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", key: "a.b", sources: ["person", "callback"])),
        ["poll input polling too often"] = Row(C.ExternalInputSource, "stages[2].steps[0].externalInputs.decision.from", "step 'deploy-beta' external input 'decision': poll intervalSeconds must be at least 10", r => Step(r, "deploy-beta")["externalInputs"] = PollExternal("https://x.example/{run.id}", 5)),
        ["poll input with a non-http url"] = Row(C.ExternalInputSource, "stages[2].steps[0].externalInputs.decision.from", "step 'deploy-beta' external input 'decision': poll url must be an absolute http(s) URL", r => Step(r, "deploy-beta")["externalInputs"] = PollExternal("ftp://x.example", 30)),

        // mapping keys / paths / types
        ["mapping key extra"] = Row(C.MappingKeyExtra, "stages[0].steps[0].input.extra", "step 'checkout': mapping 'extra' is not an input of the step", r => Step(r, "checkout")["input"]!["extra"] = Const(1)),
        ["mapping key missing"] = Row(C.MappingKeyMissing, "stages[0].steps[0].input", "step 'checkout': input 'sha' has no mapping", r => Step(r, "checkout")["input"]!.AsObject().Remove("sha")),
        ["ref path unknown"] = Row(C.RefPathUnknown, "stages[0].steps[0].input.sha", "step 'checkout' input 'sha': 'input.shaa': model DeployRequest has no field 'shaa'", r => Step(r, "checkout")["input"]!["sha"] = Ref("input.shaa")),
        ["ref path through nested object unknown"] = Row(C.RefPathUnknown, "stages[2].steps[0].input.repo", "step 'deploy-beta' input 'repo': 'build.commit.nope': model BuildArtifact has no field 'commit.nope'", r =>
        {
            Step(r, "deploy-beta")["inputTypes"]!["repo"] = StringSchema();
            Step(r, "deploy-beta")["input"]!["repo"] = Ref("build.commit.nope");
        }
        ),
        ["model input from wrong model"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.artifact", "step 'deploy-beta' input 'artifact': expects BuildArtifact but 'checkout' produces Source (models are nominal)", r => Step(r, "deploy-beta")["input"]!["artifact"] = Ref("checkout")),
        ["model input from an incompatible field"] = Row(C.MappingFieldMissing, "stages[2].steps[0].input.artifact", "step 'deploy-beta' input 'artifact': 'build.commit' has no field 'chart', which the input requires", r => Step(r, "deploy-beta")["input"]!["artifact"] = Ref("build.commit")),
        ["model input from a constant"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.artifact", "step 'deploy-beta' input 'artifact': constant is string but the input expects object", r => Step(r, "deploy-beta")["input"]!["artifact"] = Const("x")),
        ["plain input type mismatch"] = Row(C.InputTypeMismatch, "stages[0].steps[0].input.repo", "step 'checkout' input 'repo': 'input.repo' is string but the input expects number", r =>
            Step(r, "checkout")["inputTypes"]!["repo"]!["schema"]!["output"]!["type"] = "number"),
        ["const type mismatch"] = Row(C.InputTypeMismatch, "stages[0].steps[0].input.repo", "step 'checkout' input 'repo': constant is boolean but the input expects string", r => Step(r, "checkout")["input"]!["repo"] = Const(true)),
        ["nested mapping missing required field"] = Row(C.MappingFieldMissing, "stages[0].steps[0].input.commit", "step 'checkout' input 'commit': required field 'sha' is not mapped", r =>
        {
            Step(r, "checkout")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "checkout")["input"]!["commit"] = new JsonObject { ["repo"] = Ref("input.repo") };
        }
        ),
        ["nested mapping unknown field"] = Row(C.MappingFieldUnknown, "stages[0].steps[0].input.commit.extra", "step 'checkout' input 'commit': field 'extra' is not part of the input's schema", r =>
        {
            Step(r, "checkout")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "checkout")["input"]!["commit"] = new JsonObject
            { ["repo"] = Ref("input.repo"), ["sha"] = Ref("input.sha"), ["extra"] = Const(1) };
        }
        ),
        ["nested mapping with a bad ref"] = Row(C.RefPathUnknown, "stages[0].steps[0].input.commit.repo", "step 'checkout' input 'commit'.repo: 'input.nope': model DeployRequest has no field 'nope'", r =>
        {
            Step(r, "checkout")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "checkout")["input"]!["commit"] = new JsonObject { ["repo"] = Ref("input.nope"), ["sha"] = Ref("input.sha") };
        }
        ),
        ["nested mapping with a mistyped constant"] = Row(C.InputTypeMismatch, "stages[0].steps[0].input.commit.repo", "step 'checkout' input 'commit'.repo: constant is number but the input expects string", r =>
        {
            Step(r, "checkout")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "checkout")["input"]!["commit"] = new JsonObject { ["repo"] = Const(1), ["sha"] = Ref("input.sha") };
        }
        ),
        ["ref whose nested field has the wrong type"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.a' is string but the input expects number", r => MapField(r, """{"type":"object","properties":{"a":{"type":"string"}}}""", "", """{"type":"object","properties":{"a":{"type":"number"}}}""")),
        ["ref whose nested object lacks a field"] = Row(C.MappingFieldMissing, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.a' has no field 'id', which the input requires", r => MapField(r, """{"type":"object","properties":{"a":{"type":"object","properties":{}}}}""", "", """{"type":"object","properties":{"a":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}}""")),
        ["ref to a field of one oneOf branch keeps its type"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.a' is string but the input expects number", r => MapField(r, Union("oneOf"), ".a", """{"type":"number"}""")),
        ["ref to a field of one anyOf branch keeps its type"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.a' is string but the input expects number", r => MapField(r, Union("anyOf"), ".a", """{"type":"number"}""")),
        ["ref to a field of one allOf member keeps its type"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.a' is string but the input expects number", r => MapField(r, Union("allOf"), ".a", """{"type":"number"}""")),
        ["ref to a field no branch has"] = Row(C.RefPathUnknown, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.c': model BuildArtifact has no field 'x.c'", r => MapField(r, Union("oneOf"), ".c", """{"type":"string"}""")),
        ["ref into a record keeps its value type"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.any' is string but the input expects number", r => MapField(r, """{"type":"object","additionalProperties":{"type":"string"}}""", ".any", """{"type":"number"}""")),
        ["ref into a closed object"] = Row(C.RefPathUnknown, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.a': model BuildArtifact has no field 'x.a'", r => MapField(r, """{"type":"object","properties":{},"additionalProperties":false}""", ".a", """{"type":"string"}""")),
        ["ref into a string"] = Row(C.RefPathUnknown, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x.a': model BuildArtifact has no field 'x.a'", r => MapField(r, """{"type":"string"}""", ".a", """{"type":"string"}""")),
        ["integer into a string"] = Row(C.InputTypeMismatch, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': 'build.x' is number but the input expects string", r => MapField(r, """{"type":"integer"}""", "", """{"type":"string"}""")),
        ["mapped object missing a nullable anyOf object's field"] = Row(C.MappingFieldMissing, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': required field 'a' is not mapped", r => MapObject(r, NullableObject("anyOf"))),
        ["mapped object missing a nullable oneOf object's field"] = Row(C.MappingFieldMissing, "stages[2].steps[0].input.x", "step 'deploy-beta' input 'x': required field 'a' is not mapped", r => MapObject(r, NullableObject("oneOf"))),

        // when
        ["when unknown field"] = Row(C.WhenFieldUnknown, "stages[3].when.nope", "'nope' is not a field of the pipeline input DeployRequest", r => r["stages"]![3]!["when"] = new JsonObject { ["nope"] = new JsonArray("x") }),
        ["when non-literal field"] = Row(C.WhenFieldNotLiteral, "stages[3].when.sha", "'sha' is not a literal-like field (enum, boolean or literal)", r => r["stages"]![3]!["when"] = new JsonObject { ["sha"] = new JsonArray("abc") }),
        ["when invalid enum value"] = Row(C.WhenValueInvalid, "stages[3].when.deployTo", "\"prd\" is not a valid value of 'deployTo' (\"beta\", \"prod\")", r => r["stages"]![3]!["when"] = new JsonObject { ["deployTo"] = new JsonArray("prd") }),
        ["when boolean given as string"] = Row(C.WhenValueInvalid, "stages[3].stages[1].when.publishCli", "\"true\" is not a valid value of 'publishCli' (true, false)", r => r["stages"]![3]!["stages"]![1]!["when"] = new JsonObject { ["publishCli"] = new JsonArray("true") }),

        // names
        ["duplicate step name"] = Row(C.StepNameDuplicate, "stages[1].steps[1].name", "duplicate step name 'build' (also at stages[1].steps[0])", r => Step(r, "test")["name"] = "build"),
        ["reserved step name input"] = Row(C.StepNameReserved, "stages[4].steps[0].name", "'input' is reserved and cannot be a step name", r => Step(r, "summary")["name"] = "input"),
        ["reserved step name failure"] = Row(C.StepNameReserved, "failureHandlers[0].step.name", "'failure' is reserved and cannot be a step name", r => Step(r, "aoe-triage")["name"] = "failure"),
        ["step name not kebab"] = Row(C.StepNameInvalid, "stages[4].steps[0].name", "step name 'Summary_Step' must be lower-case kebab, 1-63 chars (^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$)", r => Step(r, "summary")["name"] = "Summary_Step"),
        ["step name too long"] = Row(C.StepNameInvalid, "stages[4].steps[0].name", "step name 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' must be lower-case kebab, 1-63 chars (^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$)", r => Step(r, "summary")["name"] = new string('a', 64)),
        ["step name twice in one select case"] = Row(C.StepNameDuplicate, "stages[1].cases.ssh[0].steps[1].name", "duplicate step name 'count' (also at stages[1].cases.ssh[0].steps[0])", () => Selected(r =>
        {
            var steps = r["stages"]![1]!["cases"]!["ssh"]![0]!["steps"]!.AsArray();
            steps.Add(steps[0]!.DeepClone());
        })),
        ["step name across two selects"] = Row(C.StepNameDuplicate, "stages[2].cases.ssh[0].steps[0].name", "duplicate step name 'count' (also at stages[1].cases.ssh[0].steps[0])", () => Selected(r => r["stages"]!.AsArray().Insert(2, r["stages"]![1]!.DeepClone()))),
        ["step name in different cases of two selects"] = Row(C.StepNameDuplicate, "stages[2].cases.inCluster[0].steps[0].name", "duplicate step name 'count' (also at stages[1].cases.ssh[0].steps[0])", () => Selected(r => r["stages"]!.AsArray().Insert(2, r["stages"]![1]!.DeepClone()))),
        ["handler named like a stage step"] = Row(C.StepNameDuplicate, "failureHandlers[0].step.name", "duplicate step name 'build' (also at stages[1].steps[0])", r => Step(r, "aoe-triage")["name"] = "build"),
        ["model name invalid"] = Row(C.ModelNameInvalid, "models.bad-name", "model name 'bad-name' must match ^[A-Za-z][A-Za-z0-9_]*$", r => r["models"]!["bad-name"] = new JsonObject { ["output"] = new JsonObject() }),
        ["pipeline name empty"] = Row(C.PipelineNameEmpty, "name", "the pipeline name must not be empty", r => r["name"] = " "),
        ["negative retention"] = Row(C.RetentionInvalid, "retention.keepLatestRuns", "retention.keepLatestRuns must be a non-negative integer", r => r["retention"] = new JsonObject { ["keepLatestRuns"] = -1 }),

        // models
        ["unknown output model"] = Row(C.ModelUnknown, "stages[4].steps[0].output", "step 'summary' output references model 'Nope', which is not in models", r => Step(r, "summary")["output"] = "Nope"),
        ["unknown input model"] = Row(C.ModelUnknown, "input", "pipeline input references model 'Nope', which is not in models", r => r["input"] = "Nope"),
        ["unknown inputTypes model"] = Row(C.ModelUnknown, "stages[4].steps[0].inputTypes.beta.model", "step 'summary' input 'beta' references model 'Nope', which is not in models", r => Step(r, "summary")["inputTypes"]!["beta"]!["model"] = "Nope"),

        // step fields
        ["empty step kind"] = Row(C.StepKindEmpty, "stages[1].steps[0].kind", "step 'build': kind must not be empty", r => Step(r, "build")["kind"] = ""),
        ["empty image"] = Row(C.StepImageEmpty, "stages[1].steps[0].image", "step 'build': image must not be empty", r => Step(r, "build")["image"] = " "),
        ["bad handler hash"] = Row(C.StepHashInvalid, "stages[1].steps[0].handlerHash", "step 'build': handlerHash 'sha256:abc' is not a sha256:<64 hex> hash", r => Step(r, "build")["handlerHash"] = "sha256:abc"),
        ["no handler hash and no code hash"] = Row(C.StepHashInvalid, "stages[1].steps[0].handlerHash", "step 'build': no handlerHash, and the manifest has no codeHash", r => Step(r, "build").Remove("handlerHash")),
        ["bad code hash"] = Row(C.CodeHashInvalid, "codeHash", "'sha256:abc' is not a sha256:<64 hex> hash", r => r["codeHash"] = "sha256:abc"),
        ["bad harness hash"] = Row(C.HarnessHashInvalid, "harness.sdkHash", "'md5:x' is not a sha256:<64 hex> hash", r => r["harness"]!["sdkHash"] = "md5:x"),
        ["zero timeout"] = Row(C.StepTimeoutInvalid, "stages[1].steps[0].timeoutSeconds", "step 'build': timeoutSeconds must be a positive integer", r => Step(r, "build")["timeoutSeconds"] = 0),
        ["negative retries"] = Row(C.StepRetriesInvalid, "stages[1].steps[0].retries", "step 'build': retries must be a non-negative integer", r => Step(r, "build")["retries"] = -1),

        // secrets
        ["undeclared step secret"] = Row(C.SecretUndeclared, "stages[2].steps[0].secrets[0]", "step 'deploy-beta' uses secret 'NOPE', which is not in the pipeline's secrets", r => Step(r, "deploy-beta")["secrets"] = new JsonArray("NOPE")),
        ["step secret twice"] = Row(C.SecretDuplicateInStep, "stages[2].steps[0].secrets[1]", "step 'deploy-beta' lists secret 'SSH_PRIVATE_KEY' twice", r => Step(r, "deploy-beta")["secrets"] = new JsonArray("SSH_PRIVATE_KEY", "SSH_PRIVATE_KEY")),
        ["secret name invalid"] = Row(C.SecretNameInvalid, "secrets[2].name", "secret name 'lower' must match ^[A-Z_][A-Z0-9_]*$", r => r["secrets"]!.AsArray().Add(new JsonObject { ["name"] = "lower", ["description"] = "" })),
        ["pipeline secret declared twice"] = Row(C.SecretDuplicate, "secrets[2].name", "secret 'GITHUB_TOKEN' is declared more than once", r => r["secrets"]!.AsArray().Add(new JsonObject { ["name"] = "GITHUB_TOKEN", ["description"] = "again" })),
        ["poll secret not a pipeline secret"] = Row(C.PollSecretUndeclared, "triggers[1].secrets[1]", "poll 'poll' uses secret 'OTHER_TOKEN', which is not in the pipeline's secrets", r =>
        {
            r["triggers"]![1]!["secrets"] = new JsonArray("GITHUB_TOKEN", "OTHER_TOKEN");
        }
        ),
        ["poll header secret not in poll secrets"] = Row(C.PollHeaderSecretUndeclared, "triggers[1].headers.X-Key", "poll 'poll': header 'X-Key' references secret 'SSH_PRIVATE_KEY', which is not in the poll's secrets", r =>
            r["triggers"]![1]!["headers"]!["X-Key"] = "Bearer $SECRET:SSH_PRIVATE_KEY"),

        // triggers
        ["second push trigger"] = Row(C.TriggerPushDuplicate, "triggers[2]", "there is more than one push trigger (onPush may be used once)", r => r["triggers"]!.AsArray().Add(new JsonObject { ["kind"] = "push", ["name"] = "push" })),
        ["duplicate poll name"] = Row(C.TriggerNameDuplicate, "triggers[2].name", "duplicate trigger name 'poll'", r => r["triggers"]!.AsArray().Add(r["triggers"]![1]!.DeepClone())),
        ["poll named push"] = Row(C.TriggerNameDuplicate, "triggers[1].name", "duplicate trigger name 'push'", r => r["triggers"]![1]!["name"] = "push"),
        ["poll name invalid"] = Row(C.TriggerNameInvalid, "triggers[1].name", "poll trigger name 'Poll Main' must be lower-case kebab (1-63 chars)", r => r["triggers"]![1]!["name"] = "Poll Main"),
        ["poll interval zero"] = Row(C.TriggerIntervalInvalid, "triggers[1].intervalSeconds", "poll 'poll': intervalSeconds must be a positive integer", r => r["triggers"]![1]!["intervalSeconds"] = 0),

        // failure handlers
        ["failure handler scope unknown step"] = Row(C.FailureHandlerStepUnknown, "failureHandlers[0].steps[1]", "failure handler 'aoe-triage': 'nope' is not a step of this pipeline", r => r["failureHandlers"]![0]!["steps"] = new JsonArray("deploy-prod", "nope")),
        ["failure handler input not failure"] = Row(C.FailureHandlerInputInvalid, "failureHandlers[0].step.input.failure", "step 'aoe-triage' input 'failure': a failure handler's inputs must all be FailureContext mapped from {\"ref\": \"failure\"}", r => Step(r, "aoe-triage")["input"]!["failure"] = Ref("input")),
        ["failure handler input not FailureContext"] = Row(C.FailureHandlerInputInvalid, "failureHandlers[0].step.input.failure", "step 'aoe-triage' input 'failure': a failure handler's inputs must all be FailureContext mapped from {\"ref\": \"failure\"}", r => Step(r, "aoe-triage")["inputTypes"]!["failure"] = ModelType("Source")),
        ["failure handler redefined differently"] = Row(C.FailureHandlerConflict, "failureHandlers[1].step.name", "failure handler 'aoe-triage' differs from the one at failureHandlers[0].step (one definition per step name)", r =>
        {
            var copy = r["failureHandlers"]![0]!.DeepClone();
            copy["step"]!["image"] = "busybox";
            r["failureHandlers"]!.AsArray().Add(copy);
        }
        ),
        ["failure handler waits for input"] = Row(C.ExternalInputInFailureHandler, "failureHandlers[0].step.externalInputs.decision", "step 'aoe-triage' external input 'decision': failure handlers run unattended and cannot wait for input", r => Step(r, "aoe-triage")["externalInputs"] = External("BuildArtifact")),
        // parse: shapes
        ["trigger without a kind"] = Row(C.MissingProperty, "triggers[0].kind", "a trigger needs a string 'kind' ('push', 'poll' or 'webhook')", r => r["triggers"]![0]!.AsObject().Remove("kind")),
        ["verify without a scheme"] = Row(C.MissingProperty, "triggers[2].verify.scheme", "verify needs a string 'scheme' ('hmac-sha256' or 'bearer')", r => r["triggers"]!.AsArray().Add(new JsonObject { ["kind"] = "webhook", ["name"] = "hook", ["verify"] = new JsonObject() })),
        ["unknown verify scheme"] = Row(C.InvalidValue, "triggers[2].verify.scheme", "unknown verify scheme 'md5' (expected 'hmac-sha256' or 'bearer')", r => r["triggers"]!.AsArray().Add(new JsonObject { ["kind"] = "webhook", ["name"] = "hook", ["verify"] = new JsonObject { ["scheme"] = "md5" } })),
        ["cluster access false"] = Row(C.WrongType, "stages[1].steps[0].clusterAccess", "clusterAccess must be true (omit it otherwise)", r => Step(r, "build")["clusterAccess"] = false),
        ["unknown external source"] = Row(C.WrongType, "stages[2].steps[0].externalInputs.decision.from[0].kind", "unknown external input source 'carrier' (person, callback, poll)", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", sources: ["carrier"])),
        ["external source not an object"] = Row(C.WrongType, "stages[2].steps[0].externalInputs.decision.from[0]", "expected an object, got a string", r => Step(r, "deploy-beta")["externalInputs"] = new JsonObject { ["decision"] = new JsonObject { ["model"] = "BuildArtifact", ["title"] = "Go?", ["from"] = new JsonArray("person") } }),
        ["when not an array"] = Row(C.WrongType, "stages[3].when.deployTo", "expected an array of values, got a string", r => r["stages"]![3]!["when"]!["deployTo"] = "prod"),
        ["name an object"] = Row(C.WrongType, "name", "expected a string, got an object", r => r["name"] = new JsonObject()),
        ["name an array"] = Row(C.WrongType, "name", "expected a string, got an array", r => r["name"] = new JsonArray()),
        ["name a boolean"] = Row(C.WrongType, "name", "expected a string, got a boolean", r => r["name"] = true),
        ["name null"] = Row(C.WrongType, "name", "expected a string, got null", r => r["name"] = null),
        ["stages a string"] = Row(C.WrongType, "stages", "expected an array, got a string", r => r["stages"] = "x"),
        ["models an array"] = Row(C.WrongType, "models", "expected an object, got an array", r => r["models"] = new JsonArray()),
        ["optional not a boolean"] = Row(C.WrongType, "stages[2].steps[0].inputTypes.artifact.optional", "expected a boolean, got a string", r => Step(r, "deploy-beta")["inputTypes"]!["artifact"]!["optional"] = "no"),
        ["trigger not an object"] = Row(C.WrongType, "triggers[0]", "expected an object, got a number", r => r["triggers"]![0] = 1),
        ["verify not an object"] = Row(C.WrongType, "triggers[2].verify", "expected an object, got a number", r => r["triggers"]!.AsArray().Add(new JsonObject { ["kind"] = "webhook", ["name"] = "hook", ["verify"] = 1 })),
        ["stage not an object"] = Row(C.WrongType, "stages[0]", "expected an object, got a number", r => r["stages"]![0] = 1),
        ["input type not an object"] = Row(C.WrongType, "stages[1].steps[0].inputTypes.source", "expected an object, got a number", r => Step(r, "build")["inputTypes"]!["source"] = 1),
        ["model input view not an object"] = Row(C.WrongType, "models.Source.input", "expected a JSON Schema object, got a number", r => r["models"]!["Source"]!["input"] = 1),
        ["poll header not a string"] = Row(C.WrongType, "triggers[1].headers.Authorization", "expected a string, got a number", r => r["triggers"]![1]!["headers"]!["Authorization"] = 1),
        ["select case not an array"] = Row(C.WrongType, "stages[1].cases.ssh", "expected an array, got a number", () => Selected(r => r["stages"]![1]!["cases"]!["ssh"] = 1)),
        ["config schema not an object"] = Row(C.WrongType, "config[0].schema", "expected a JSON Schema object, got a number", () => Configured(r => r["config"]![0]!["schema"] = 1)),

        // names and limits
        ["negative minRunAgeDays"] = Row(C.RetentionInvalid, "retention.minRunAgeDays", "retention.minRunAgeDays must be a non-negative integer", r => r["retention"] = new JsonObject { ["minRunAgeDays"] = -1 }),
        ["negative buildGraceDays"] = Row(C.RetentionInvalid, "retention.buildGraceDays", "retention.buildGraceDays must be a non-negative integer", r => r["retention"] = new JsonObject { ["buildGraceDays"] = -1 }),
        ["poll secret name invalid"] = Row(C.SecretNameInvalid, "triggers[1].secrets[0]", "secret name 'lower' must match ^[A-Z_][A-Z0-9_]*$", r => r["triggers"]![1]!["secrets"] = new JsonArray("lower")),
        ["step secret name invalid"] = Row(C.SecretNameInvalid, "stages[1].steps[0].secrets[0]", "secret name 'lower' must match ^[A-Z_][A-Z0-9_]*$", r => Step(r, "build")["secrets"] = new JsonArray("lower")),

        // webhooks
        ["webhook name invalid"] = Row(C.TriggerNameInvalid, "triggers[2].name", "webhook trigger name 'Bad Hook' must be lower-case kebab (1-63 chars)", r => r["triggers"]!.AsArray().Add(Webhook("Bad Hook"))),
        ["webhook secret undeclared"] = Row(C.WebhookSecretUndeclared, "triggers[2].verify.secret", "webhook 'hook' verifies with secret 'NOPE', which is not in the pipeline's secrets", r => r["triggers"]!.AsArray().Add(Webhook(secret: "NOPE"))),
        ["webhook empty signature header"] = Row(C.InvalidValue, "triggers[2].verify.header", "webhook 'hook': the signature header must not be empty", r => r["triggers"]!.AsArray().Add(Webhook(header: ""))),

        // external inputs and mappings
        ["external input key reserved"] = Row(C.ExternalInputClash, "stages[2].steps[0].externalInputs.$config", "step 'deploy-beta' external input '$config': keys starting with '$' are reserved", r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", key: "$config")),
        ["poll input with an empty value path"] = Row(C.ExternalInputSource, "stages[2].steps[0].externalInputs.decision.from", "step 'deploy-beta' external input 'decision': poll valuePath must not be empty", r => Step(r, "deploy-beta")["externalInputs"] = PollExternal("https://x.example", 30, valuePath: " ")),
        ["object mapped to a string input"] = Row(C.InputTypeMismatch, "stages[0].steps[0].input.sha", "step 'checkout' input 'sha': an object is mapped but the input expects string", r => Step(r, "checkout")["input"]!["sha"] = new JsonObject { ["a"] = Const(1) }),
        ["optional field into a required one"] = Row(C.MappingFieldMissing, "stages[2].steps[0].input.commit", "step 'deploy-beta' input 'commit': 'build.commit.sha' is optional but the input requires it", r =>
        {
            r["models"]!["BuildArtifact"]!["output"]!["properties"]!["commit"] = JsonNode.Parse("""{"type":"object","properties":{"repo":{"type":"string"},"sha":{"type":"string"}},"required":["repo"]}""");
            Step(r, "deploy-beta")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "deploy-beta")["input"]!["commit"] = Ref("build.commit");
        }
        ),

        // config
        ["config name invalid"] = Row(C.ConfigNameInvalid, "config[1].name", "config entry name 'Target' must match ^[a-z][a-zA-Z0-9]*$", () => Configured(r => r["config"]![1]!["name"] = "Target")),
        ["config declared twice"] = Row(C.ConfigDuplicate, "config[2].name", "config entry 'target' is declared more than once", () => Configured(r => r["config"]!.AsArray().Add(r["config"]![1]!.DeepClone()))),
        ["step lists a config entry twice"] = Row(C.ConfigDuplicate, "stages[0].steps[0].configEntries[2]", "step 'hello' lists config entry 'target' twice", () => Configured(r => r["stages"]![0]!["steps"]![0]!["configEntries"] = new JsonArray("auth", "target", "target"))),
        ["step uses an unknown config entry"] = Row(C.ConfigUnknown, "stages[0].steps[0].configEntries[1]", "step 'hello' uses config entry 'nope', which is not in the pipeline's config", () => Configured(r => r["stages"]![0]!["steps"]![0]!["configEntries"] = new JsonArray("auth", "nope"))),
        ["config references an undeclared secret"] = Row(C.ConfigSecretRefUndeclared, "config[0].schema", "config entry 'auth' references secret 'DEX_CLIENT_SECRET', which is not in the pipeline's secrets", () => Configured(r => r["secrets"] = new JsonArray())),
        ["step lacks the config's secret"] = Row(C.ConfigSecretRefNotInStep, "stages[0].steps[0].configEntries[0]", "step 'hello' receives config entry 'auth', which references secret 'DEX_CLIENT_SECRET'; the step must also declare that secret", () => ConfigManifests.Text(helloDeclaresSecret: false)),
        ["config ref to an entry not received"] = Row(C.ConfigRefNotReceived, "stages[0].steps[0].config.x", "step 'hello': config reference 'publish.repository': the step does not receive config entry 'publish'", () => Configured(r => r["stages"]![0]!["steps"]![0]!["config"] = ConfigRef("publish.repository"))),
        ["config ref to an unknown field"] = Row(C.ConfigRefFieldUnknown, "stages[0].steps[0].config.x", "step 'hello': config reference 'target.port': config 'target' has no field 'port'", () => Configured(r => r["stages"]![0]!["steps"]![0]!["config"] = ConfigRef("target.port"))),
        ["config ref narrowed to another case"] = Row(C.ConfigRefOutsideCase, "stages[1].cases.ssh[0].steps[0].config.target", "step 'count': config reference 'target' is narrowed to the select case 'target.kind=inCluster' and can only be used inside it", () => Selected(r => r["stages"]![1]!["cases"]!["ssh"]![0]!["steps"]![0]!["config"]!["target"]!["$case"] = "target.kind=inCluster")),
        ["config ref nested in an object"] = Row(C.ConfigRefFieldUnknown, "stages[0].steps[0].config.a.b", "step 'hello': config reference 'target.port': config 'target' has no field 'port'", () => Configured(r => r["stages"]![0]!["steps"]![0]!["config"] = new JsonObject { ["a"] = new JsonObject { ["b"] = new JsonObject { ["$config"] = "target.port" } } })),
        ["config ref nested in an array"] = Row(C.ConfigRefFieldUnknown, "stages[0].steps[0].config.a[1]", "step 'hello': config reference 'target.port': config 'target' has no field 'port'", () => Configured(r => r["stages"]![0]!["steps"]![0]!["config"] = new JsonObject { ["a"] = new JsonArray("plain", new JsonObject { ["$config"] = "target.port" }) })),
        ["config ref narrowed outside any select"] = Row(C.ConfigRefOutsideCase, "stages[0].steps[0].config.x", "step 'hello': config reference 'target.host' is narrowed to the select case 'target.kind=ssh' and can only be used inside it", () => Selected(r => r["stages"]![0]!["steps"]![0]!["config"] = new JsonObject { ["x"] = new JsonObject { ["$config"] = "target.host", ["$case"] = "target.kind=ssh" } })),
        ["config ref narrowed in a failure handler"] = Row(C.ConfigRefOutsideCase, "failureHandlers[0].step.config.x", "step 'log-failure': config reference 'target.host' is narrowed to the select case 'target.kind=ssh' and can only be used inside it", () => Selected(r =>
        {
            var handler = r["failureHandlers"]![0]!["step"]!;
            handler["configEntries"] = new JsonArray("target");
            handler["config"] = new JsonObject { ["x"] = new JsonObject { ["$config"] = "target.host", ["$case"] = "target.kind=ssh" } };
        })),
        ["config ref to the secret part of an entry"] = Row(C.ConfigSecretRefNotInStep, "stages[0].steps[0].configEntries[0]", "step 'hello' receives config entry 'auth', which references secret 'DEX_CLIENT_SECRET'; the step must also declare that secret", () => HelloRefersTo("auth.clientSecret")),
        ["config ref to another entry leaves this one whole"] = Row(C.ConfigSecretRefNotInStep, "stages[0].steps[0].configEntries[0]", "step 'hello' receives config entry 'auth', which references secret 'DEX_CLIENT_SECRET'; the step must also declare that secret", () => HelloRefersTo("target.host")),
        ["config ref into a record of secrets"] = Row(C.ConfigSecretRefNotInStep, "stages[0].steps[0].configEntries[0]", "step 'hello' receives config entry 'auth', which references secret 'DEX_CLIENT_SECRET'; the step must also declare that secret", () => HelloRefersTo("auth.keys.foo", schema => schema["keys"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = JsonNode.Parse(SecretRefSchema) })),
        ["config ref not narrowed needs every branch's secret"] = Row(C.ConfigSecretRefNotInStep, "stages[1].cases.ssh[0].steps[0].configEntries[0]", "step 'count' receives config entry 'target', which references secret 'KUBE_KEY'; the step must also declare that secret", () => Selected(r =>
        {
            SelectTests.SecretPerCase(r);
            r["stages"]![1]!["cases"]!["ssh"]![0]!["steps"]![0]!["config"]!["target"]!.AsObject().Remove("$case");
        })),

        // select
        ["step in only one select case"] = Row(C.RefMaybeAvailable, "stages[3].stages[0].steps[0].input.count", "step 'last' input 'count': LineCount from 'count' is only maybe-available (produced inside a when block); declare it optional(LineCount)", () => Selected(r => r["stages"]![1]!["cases"]!["inCluster"]![0]!["steps"]![0]!["name"] = "count-in-cluster")),
        ["step under a when in one select case"] = Row(C.RefMaybeAvailable, "stages[3].stages[0].steps[0].input.count", "step 'last' input 'count': LineCount from 'count' is only maybe-available (produced inside a when block); declare it optional(LineCount)", () => Selected(r =>
        {
            var cases = r["stages"]![1]!["cases"]!.AsObject();
            var stages = cases["ssh"]!.DeepClone();
            cases["ssh"] = new JsonArray(new JsonObject { ["when"] = new JsonObject { ["mode"] = new JsonArray("normal") }, ["stages"] = stages });
        })),
        ["select of an unknown entry"] = Row(C.SelectInvalid, "stages[1].select", "select 'nope': 'nope' is not in the pipeline's config", () => Selected(r => r["stages"]![1]!["select"] = "nope")),
        ["select on a field that isn't a union"] = Row(C.SelectInvalid, "stages[1].select", "select 'auth.mode': it must reference a union whose branches each fix 'kind' to a string", () => Selected(r => r["stages"]![1]!["select"] = "auth.mode")),
        ["select without a case for a value"] = Row(C.SelectCaseMissing, "stages[1].cases", "select 'target': no case for kind 'inCluster'", () => Selected(r => r["stages"]![1]!["cases"]!.AsObject().Remove("inCluster"))),
        ["select with a case for no value"] = Row(C.SelectCaseUnknown, "stages[1].cases.docker", "select 'target': 'docker' is not a value of kind (ssh, inCluster)", () => Selected(r => r["stages"]![1]!["cases"]!["docker"] = new JsonArray())),
        ["select cases disagreeing on output"] = Row(C.SelectOutputMismatch, "stages[1].cases.inCluster[0].steps[0].output", "step 'count' has output LineCount here and none at stages[1].cases.ssh[0].steps[0]; select cases must agree", () => Selected(r => r["stages"]![1]!["cases"]!["ssh"]![0]!["steps"]![0]!["output"] = null)),

        // matrix
        ["matrix with no axes"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.axes", "step 'test': a matrix has 1 to 4 axes (it has 0)", r =>
        {
            var matrix = Matrix();
            matrix["axes"] = new JsonArray();
            Step(r, "test")["matrix"] = matrix;
        }
        ),
        ["matrix with five axes"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.axes", "step 'test': a matrix has 1 to 4 axes (it has 5)", r => Step(r, "test")["matrix"] = Matrix(axes: [("a", ["1"]), ("b", ["1"]), ("c", ["1"]), ("d", ["1"]), ("e", ["1"])])),
        ["matrix axis name"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.axes[0].name", "step 'test': matrix axis name 'OS' must match ^[a-z][a-zA-Z0-9]{0,30}$", r => Step(r, "test")["matrix"] = Matrix(axes: [("OS", ["linux"])])),
        ["matrix axis twice"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.axes[1].name", "step 'test': matrix axis 'os' is declared more than once", r => Step(r, "test")["matrix"] = Matrix(axes: [("os", ["linux"]), ("os", ["macos"])])),
        ["matrix axis without values"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.axes[0].values", "step 'test': matrix axis 'os' has no values", r => Step(r, "test")["matrix"] = Matrix(axes: [("os", [])])),
        ["matrix value"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.axes[0].values[1]", "step 'test': matrix value 'mac os' of axis 'os' must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$", r => Step(r, "test")["matrix"] = Matrix(axes: [("os", ["linux", "mac os"])])),
        ["matrix value twice"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.axes[0].values[1]", "step 'test': matrix axis 'os' lists value 'linux' twice", r => Step(r, "test")["matrix"] = Matrix(axes: [("os", ["linux", "linux"])])),
        ["matrix exclude naming nothing"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.exclude[0]", "step 'test': a matrix exclude entry names at least one axis", r => Step(r, "test")["matrix"] = Matrix(exclude: [new JsonObject()])),
        ["matrix exclude of an unknown axis"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.exclude[0].cpu", "step 'test': matrix exclude names axis 'cpu', which the matrix does not have", r => Step(r, "test")["matrix"] = Matrix(exclude: [new JsonObject { ["cpu"] = "x64" }])),
        ["matrix exclude of an unknown value"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.exclude[0].os", "step 'test': matrix exclude value 'macos' is not a value of axis 'os' (linux, windows)", r => Step(r, "test")["matrix"] = Matrix(exclude: [new JsonObject { ["os"] = "macos" }])),
        ["matrix failure strategy"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.failure.kind", "step 'test': unknown matrix failure strategy 'failFast' (keepRunning, cascadeFailure)", r => Step(r, "test")["matrix"] = Matrix(failure: "failFast")),
        ["matrix maxParallel"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix.maxParallel", "step 'test': maxParallel must be at least 1", r => Step(r, "test")["matrix"] = Matrix(maxParallel: 0)),
        ["matrix excluding every cell"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix", "step 'test': the matrix has 0 cells; it needs 1 to 64", r => Step(r, "test")["matrix"] = Matrix(exclude: [new JsonObject { ["os"] = "linux" }, new JsonObject { ["os"] = "windows" }])),
        ["matrix with too many cells"] = Row(C.MatrixInvalid, "stages[1].steps[1].matrix", "step 'test': the matrix has 125 cells; it needs 1 to 64", r => Step(r, "test")["matrix"] = Matrix(axes: [("a", ["1", "2", "3", "4", "5"]), ("b", ["1", "2", "3", "4", "5"]), ("c", ["1", "2", "3", "4", "5"])])),
        ["matrix step with an external input"] = Row(C.MatrixInvalid, "stages[1].steps[1].externalInputs", "step 'test': a matrix step can't wait for external inputs", r =>
        {
            Step(r, "test")["matrix"] = Matrix();
            Step(r, "test")["externalInputs"] = External("BuildArtifact");
        }
        ),
        ["matrix failure handler"] = Row(C.MatrixInFailureHandler, "failureHandlers[0].step.matrix", "failure handler 'aoe-triage': a failure handler can't be a matrix step", r => r["failureHandlers"]![0]!["step"]!["matrix"] = Matrix()),
        ["matrix step read by a model input"] = Row(C.RefMatrixStep, "stages[2].steps[0].input.artifact", "step 'deploy-beta' input 'artifact': 'build' is a matrix step; read it through a cells input (a list of its cells' outputs)", r => Step(r, "build")["matrix"] = Matrix()),
        ["matrix step field read"] = Row(C.RefMatrixStep, "stages[2].steps[0].input.version", "step 'deploy-beta' input 'version': 'build' is a matrix step; read it through a cells input (a list of its cells' outputs)", r =>
        {
            MatrixBuild(r);
            Step(r, "deploy-beta")["inputTypes"]!["version"] = StringSchema();
            Step(r, "deploy-beta")["input"]!["version"] = Ref("build.version");
        }
        ),
        ["cells input of a plain step"] = Row(C.CellsInputInvalid, "stages[2].steps[0].input.artifact", "step 'deploy-beta' input 'artifact': a cells input reads a matrix step, and 'build' is not one", r => Step(r, "deploy-beta")["inputTypes"]!["artifact"] = CellsType("BuildArtifact")),
        ["cells input of a field"] = Row(C.CellsInputInvalid, "stages[2].steps[0].input.artifact", "step 'deploy-beta' input 'artifact': a cells input maps a whole matrix step ({\"ref\": \"<step>\"})", r =>
        {
            MatrixBuild(r);
            Step(r, "deploy-beta")["input"]!["artifact"] = Ref("build.commit");
        }
        ),
        ["cells input of a maybe-skipped matrix step"] = Row(C.CellsInputInvalid, "stages[4].steps[0].input.prod", "step 'summary' input 'prod': matrix step 'deploy-prod' may be skipped (it runs inside a when block); a cells input reads a matrix step every run has", r =>
        {
            Step(r, "deploy-prod")["matrix"] = Matrix();
            Step(r, "summary")["inputTypes"]!["prod"] = CellsType("ProdDeployment", optional: true);
            Step(r, "publish-cli")["inputTypes"]!["prod"] = CellsType("ProdDeployment");
        }
        ),
        ["select cases disagreeing on matrix"] = Row(C.MatrixInvalid, "stages[1].cases.inCluster[0].steps[0].matrix", "step 'count' is not a matrix step here and is at stages[1].cases.ssh[0].steps[0]; select cases must agree", () => Selected(r => r["stages"]![1]!["cases"]!["ssh"]![0]!["steps"]![0]!["matrix"] = Matrix())),
        ["cells false"] = Row(C.WrongType, "stages[2].steps[0].inputTypes.artifact.cells", "cells must be true (omit it otherwise)", r => Step(r, "deploy-beta")["inputTypes"]!["artifact"]!["cells"] = false),
        ["unknown matrix property"] = Row(C.UnknownProperty, "stages[1].steps[1].matrix.include", "unknown property 'include'", r =>
        {
            var matrix = Matrix();
            matrix["include"] = new JsonArray();
            Step(r, "test")["matrix"] = matrix;
        }
        ),

        // warnings
        ["unused output"] = Row(C.OutputUnused, "stages[1].steps[1].output", "the output of step 'test' is not used by any later step (fine for gate steps)", _ => { }),
        ["unused model"] = Row(C.ModelUnused, "models.Spare", "model 'Spare' is not referenced by the pipeline", r => r["models"]!["Spare"] = new JsonObject { ["output"] = new JsonObject() }),
    };

    private static JsonObject External(string model, string key = "decision", string title = "Go?", string[]? sources = null) => new()
    {
        [key] = new JsonObject
        {
            ["model"] = model,
            ["title"] = title,
            ["from"] = new JsonArray((sources ?? ["person"]).Select(k => (JsonNode)new JsonObject { ["kind"] = k }).ToArray()),
        },
    };

    private static JsonObject Webhook(string name = "hook", string secret = "GITHUB_TOKEN", string header = "X-Hub-Signature-256") => new()
    {
        ["kind"] = "webhook",
        ["name"] = name,
        ["verify"] = new JsonObject { ["scheme"] = "hmac-sha256", ["secret"] = secret, ["header"] = header, ["prefix"] = "sha256=" },
    };

    private static string Union(string combinator) =>
        "{\"" + combinator + "\":[{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}},{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"number\"}}}]}";

    private static string NullableObject(string combinator) =>
        "{\"" + combinator + "\":[{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}},\"required\":[\"a\"]},{\"type\":\"null\"}]}";

    private static string HelloRefersTo(string reference, Action<JsonObject>? authProperties = null)
    {
        var node = JsonNode.Parse(ConfigManifests.Text(helloDeclaresSecret: false))!.AsObject();
        node["stages"]![0]!["steps"]![0]!["config"] = ConfigRef(reference);
        if (authProperties is not null)
        {
            var auth = node["config"]!.AsArray().Single(e => (string?)e!["name"] == "auth")!["schema"]!.AsObject();
            auth.Clear();
            auth["type"] = "object";
            var properties = new JsonObject();
            authProperties(properties);
            auth["properties"] = properties;
        }
        return node.ToJsonString();
    }

    private const string SecretRefSchema = """{"type":"object","properties":{"secret":{"type":"string","const":"DEX_CLIENT_SECRET"}},"required":["secret"],"additionalProperties":false,"x-olve-kind":"secretRef"}""";

    private static JsonObject ConfigRef(string reference) => new() { ["x"] = new JsonObject { ["$config"] = reference } };

    private static string Configured(Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(ConfigManifests.Text())!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static string Selected(Action<JsonObject> edit) => SelectTests.Node(edit).ToJsonString();

    private static JsonObject PollExternal(string url, int interval, string valuePath = "v") => new()
    {
        ["decision"] = new JsonObject
        {
            ["model"] = "BuildArtifact",
            ["title"] = "Go?",
            ["from"] = new JsonArray(new JsonObject { ["kind"] = "poll", ["url"] = url, ["valuePath"] = valuePath, ["intervalSeconds"] = interval }),
        },
    };

    public static IEnumerable<string> CaseNames() => Cases.Keys;

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task EachIssue_HasItsExactPathAndMessage(string name)
    {
        var c = Cases[name];
        var json = c.Json();

        var (issues, failed) = Check(json);

        await Assert.That(issues.Where(i => i.Code == c.Code).Select(i => (i.Path, OwnText(i)))).Contains((c.Path, c.Message));
        await Assert.That(failed).IsEqualTo(c.Code is not (C.OutputUnused or C.ModelUnused));
    }

    [Test]
    public async Task EveryIssueCode_HasARow()
    {
        var codes = typeof(C).GetFields().Select(f => (string)f.GetRawConstantValue()!);

        await Assert.That(codes.Except(Cases.Values.Select(c => c.Code))).IsEmpty();
    }

    private static (IReadOnlyList<ManifestIssue> Issues, bool Failed) Check(string json)
    {
        if (!ManifestParser.TryParse(Encoding.UTF8.GetBytes(json), out var manifest, out var issues))
            return (issues, ManifestParser.Parse(json).Failed);
        var report = ManifestValidator.Validate(manifest);
        return ([.. report.Errors, .. report.Warnings], report.ToResult().Failed);
    }

    // After the prefix an invalid-JSON message is the framework's own text.
    private static string OwnText(ManifestIssue issue) =>
        issue.Code == C.InvalidJson ? issue.Message[..(issue.Message.IndexOf(": ", StringComparison.Ordinal) + 2)] : issue.Message;
}
