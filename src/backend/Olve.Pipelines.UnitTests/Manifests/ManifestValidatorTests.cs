using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Manifests.ManifestFixture;
using C = Olve.Pipelines.Pipelines.Manifests.ManifestIssueCodes;

namespace Olve.Pipelines.UnitTests.Manifests;

public class ManifestValidatorTests
{
    // Fixture layout: stages[0] checkout · stages[1] build, test · stages[2] deploy-beta ·
    // stages[3] when{deployTo:prod} [deploy-prod, when{publishCli:true} [publish-cli]] · stages[4] summary;
    // failureHandlers[0] aoe-triage (deploy-prod, deploy-beta); triggers: push, poll.
    private static readonly Dictionary<string, (string Code, Action<JsonObject> Mutate)> FailingCases = new()
    {
        // availability
        ["same-stage sibling"] = (C.RefUnavailable, r =>
        {
            Step(r, "test")["inputTypes"]!["source"] = ModelType("BuildArtifact");
            Step(r, "test")["input"]!["source"] = Ref("build");
        }
        ),
        ["later-stage ref"] = (C.RefUnavailable, r => Step(r, "checkout")["input"]!["sha"] = Ref("build.version")),
        ["unknown root"] = (C.RefUnavailable, r => Step(r, "deploy-beta")["input"]!["artifact"] = Ref("nope")),
        ["optional with no producer"] = (C.RefUnavailable, r => Step(r, "summary")["input"]!["cli"] = Ref("publish-cli-x")),
        ["maybe-available as required model input"] = (C.RefMaybeAvailable, r => Step(r, "summary")["inputTypes"]!["prod"]!["optional"] = false),
        ["maybe-available field into plain input"] = (C.RefMaybeAvailable, r =>
        {
            Step(r, "summary")["inputTypes"]!["url"] = StringSchema();
            Step(r, "summary")["input"]!["url"] = Ref("deploy-prod.url");
        }
        ),
        ["ref to step without output"] = (C.RefNoOutput, r =>
        {
            Step(r, "deploy-beta")["output"] = null;
        }
        ),
        ["failure root outside handler"] = (C.RefFailureOutsideHandler, r => Step(r, "checkout")["input"]!["sha"] = Ref("failure.step")),
        ["ref syntax"] = (C.RefSyntaxInvalid, r => Step(r, "checkout")["input"]!["sha"] = Ref("input..sha")),

        // external inputs
        ["external input of an unknown model"] = (C.ModelUnknown, r => Step(r, "deploy-beta")["externalInputs"] = External("Nope")),
        ["external input named like an input"] = (C.ExternalInputClash, r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", key: "artifact")),
        ["external input without a source"] = (C.ExternalInputNoSource, r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", sources: [])),
        ["external input with an empty title"] = (C.ExternalInputTitleEmpty, r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", title: " ")),
        ["external input listing a source twice"] = (C.ExternalInputSource, r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", sources: ["person", "person"])),
        ["callback input with a key that isn't URL-safe"] = (C.ExternalInputSource, r => Step(r, "deploy-beta")["externalInputs"] = External("BuildArtifact", key: "a.b", sources: ["callback"])),
        ["poll input polling too often"] = (C.ExternalInputSource, r => Step(r, "deploy-beta")["externalInputs"] = PollExternal("https://x.example/{run.id}", 5)),
        ["poll input with a non-http url"] = (C.ExternalInputSource, r => Step(r, "deploy-beta")["externalInputs"] = PollExternal("ftp://x.example", 30)),

        // mapping keys / paths / types
        ["mapping key extra"] = (C.MappingKeyExtra, r => Step(r, "checkout")["input"]!["extra"] = Const(1)),
        ["mapping key missing"] = (C.MappingKeyMissing, r => Step(r, "checkout")["input"]!.AsObject().Remove("sha")),
        ["ref path unknown"] = (C.RefPathUnknown, r => Step(r, "checkout")["input"]!["sha"] = Ref("input.shaa")),
        ["ref path through nested object unknown"] = (C.RefPathUnknown, r =>
        {
            Step(r, "deploy-beta")["inputTypes"]!["repo"] = StringSchema();
            Step(r, "deploy-beta")["input"]!["repo"] = Ref("build.commit.nope");
        }
        ),
        ["model input from wrong model"] = (C.InputTypeMismatch, r => Step(r, "deploy-beta")["input"]!["artifact"] = Ref("checkout")),
        ["model input from an incompatible field"] = (C.MappingFieldMissing, r => Step(r, "deploy-beta")["input"]!["artifact"] = Ref("build.commit")),
        ["model input from a constant"] = (C.InputTypeMismatch, r => Step(r, "deploy-beta")["input"]!["artifact"] = Const("x")),
        ["plain input type mismatch"] = (C.InputTypeMismatch, r =>
            Step(r, "checkout")["inputTypes"]!["repo"]!["schema"]!["output"]!["type"] = "number"),
        ["const type mismatch"] = (C.InputTypeMismatch, r => Step(r, "checkout")["input"]!["repo"] = Const(true)),
        ["nested mapping missing required field"] = (C.MappingFieldMissing, r =>
        {
            Step(r, "checkout")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "checkout")["input"]!["commit"] = new JsonObject { ["repo"] = Ref("input.repo") };
        }
        ),
        ["nested mapping unknown field"] = (C.MappingFieldUnknown, r =>
        {
            Step(r, "checkout")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "checkout")["input"]!["commit"] = new JsonObject
            { ["repo"] = Ref("input.repo"), ["sha"] = Ref("input.sha"), ["extra"] = Const(1) };
        }
        ),

        // when
        ["when unknown field"] = (C.WhenFieldUnknown, r => r["stages"]![3]!["when"] = new JsonObject { ["nope"] = new JsonArray("x") }),
        ["when non-literal field"] = (C.WhenFieldNotLiteral, r => r["stages"]![3]!["when"] = new JsonObject { ["sha"] = new JsonArray("abc") }),
        ["when invalid enum value"] = (C.WhenValueInvalid, r => r["stages"]![3]!["when"] = new JsonObject { ["deployTo"] = new JsonArray("prd") }),
        ["when boolean given as string"] = (C.WhenValueInvalid, r => r["stages"]![3]!["stages"]![1]!["when"] = new JsonObject { ["publishCli"] = new JsonArray("true") }),

        // names
        ["duplicate step name"] = (C.StepNameDuplicate, r => Step(r, "test")["name"] = "build"),
        ["reserved step name input"] = (C.StepNameReserved, r => Step(r, "summary")["name"] = "input"),
        ["reserved step name failure"] = (C.StepNameReserved, r => Step(r, "aoe-triage")["name"] = "failure"),
        ["step name not kebab"] = (C.StepNameInvalid, r => Step(r, "summary")["name"] = "Summary_Step"),
        ["step name too long"] = (C.StepNameInvalid, r => Step(r, "summary")["name"] = new string('a', 64)),
        ["handler named like a stage step"] = (C.StepNameDuplicate, r => Step(r, "aoe-triage")["name"] = "build"),
        ["model name invalid"] = (C.ModelNameInvalid, r => r["models"]!["bad-name"] = new JsonObject { ["output"] = new JsonObject() }),
        ["pipeline name empty"] = (C.PipelineNameEmpty, r => r["name"] = " "),
        ["negative retention"] = (C.RetentionInvalid, r => r["retention"] = new JsonObject { ["keepLatestRuns"] = -1 }),

        // models
        ["unknown output model"] = (C.ModelUnknown, r => Step(r, "summary")["output"] = "Nope"),
        ["unknown input model"] = (C.ModelUnknown, r => r["input"] = "Nope"),
        ["unknown inputTypes model"] = (C.ModelUnknown, r => Step(r, "summary")["inputTypes"]!["beta"]!["model"] = "Nope"),

        // step fields
        ["empty step kind"] = (C.StepKindEmpty, r => Step(r, "build")["kind"] = ""),
        ["empty image"] = (C.StepImageEmpty, r => Step(r, "build")["image"] = " "),
        ["bad handler hash"] = (C.StepHashInvalid, r => Step(r, "build")["handlerHash"] = "sha256:abc"),
        ["no handler hash and no code hash"] = (C.StepHashInvalid, r => Step(r, "build").Remove("handlerHash")),
        ["bad code hash"] = (C.CodeHashInvalid, r => r["codeHash"] = "sha256:abc"),
        ["bad harness hash"] = (C.HarnessHashInvalid, r => r["harness"]!["sdkHash"] = "md5:x"),
        ["zero timeout"] = (C.StepTimeoutInvalid, r => Step(r, "build")["timeoutSeconds"] = 0),
        ["negative retries"] = (C.StepRetriesInvalid, r => Step(r, "build")["retries"] = -1),

        // secrets
        ["undeclared step secret"] = (C.SecretUndeclared, r => Step(r, "deploy-beta")["secrets"] = new JsonArray("NOPE")),
        ["step secret twice"] = (C.SecretDuplicateInStep, r => Step(r, "deploy-beta")["secrets"] = new JsonArray("SSH_PRIVATE_KEY", "SSH_PRIVATE_KEY")),
        ["secret name invalid"] = (C.SecretNameInvalid, r => r["secrets"]!.AsArray().Add(new JsonObject { ["name"] = "lower", ["description"] = "" })),
        ["pipeline secret declared twice"] = (C.SecretDuplicate, r => r["secrets"]!.AsArray().Add(new JsonObject { ["name"] = "GITHUB_TOKEN", ["description"] = "again" })),
        ["poll secret not a pipeline secret"] = (C.PollSecretUndeclared, r =>
        {
            r["triggers"]![1]!["secrets"] = new JsonArray("GITHUB_TOKEN", "OTHER_TOKEN");
        }
        ),
        ["poll header secret not in poll secrets"] = (C.PollHeaderSecretUndeclared, r =>
            r["triggers"]![1]!["headers"]!["X-Key"] = "Bearer $SECRET:SSH_PRIVATE_KEY"),

        // triggers
        ["second push trigger"] = (C.TriggerPushDuplicate, r => r["triggers"]!.AsArray().Add(new JsonObject { ["kind"] = "push", ["name"] = "push" })),
        ["duplicate poll name"] = (C.TriggerNameDuplicate, r => r["triggers"]!.AsArray().Add(r["triggers"]![1]!.DeepClone())),
        ["poll named push"] = (C.TriggerNameDuplicate, r => r["triggers"]![1]!["name"] = "push"),
        ["poll name invalid"] = (C.TriggerNameInvalid, r => r["triggers"]![1]!["name"] = "Poll Main"),
        ["poll interval zero"] = (C.TriggerIntervalInvalid, r => r["triggers"]![1]!["intervalSeconds"] = 0),

        // failure handlers
        ["failure handler scope unknown step"] = (C.FailureHandlerStepUnknown, r => r["failureHandlers"]![0]!["steps"] = new JsonArray("deploy-prod", "nope")),
        ["failure handler input not failure"] = (C.FailureHandlerInputInvalid, r => Step(r, "aoe-triage")["input"]!["failure"] = Ref("input")),
        ["failure handler input not FailureContext"] = (C.FailureHandlerInputInvalid, r => Step(r, "aoe-triage")["inputTypes"]!["failure"] = ModelType("Source")),
        ["failure handler redefined differently"] = (C.FailureHandlerConflict, r =>
        {
            var copy = r["failureHandlers"]![0]!.DeepClone();
            copy["step"]!["image"] = "busybox";
            r["failureHandlers"]!.AsArray().Add(copy);
        }
        ),
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

    private static JsonObject PollExternal(string url, int interval) => new()
    {
        ["decision"] = new JsonObject
        {
            ["model"] = "BuildArtifact",
            ["title"] = "Go?",
            ["from"] = new JsonArray(new JsonObject { ["kind"] = "poll", ["url"] = url, ["valuePath"] = "v", ["intervalSeconds"] = interval }),
        },
    };

    private static JsonNode CommitSchema() => new JsonObject
    {
        ["schema"] = new JsonObject
        {
            ["output"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["repo"] = new JsonObject { ["type"] = "string" },
                    ["sha"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("repo", "sha"),
            },
        },
    };

    public static IEnumerable<string> FailingCaseNames() => FailingCases.Keys;

    [Test]
    public async Task SelfDeployFixture_IsValid_WithOnlyTheUnusedTestReportWarning()
    {
        var report = ManifestValidator.Validate(Parse());

        await Assert.That(report.Errors).IsEmpty();
        await Assert.That(report.Warnings.Select(w => (w.Code, w.Step))).IsEquivalentTo([(C.OutputUnused, (string?)"test")]);
        await Assert.That(report.ToResult()).Succeeded();
    }

    [Test]
    [MethodDataSource(nameof(FailingCaseNames))]
    public async Task EachCheck_RejectsItsMutation(string name)
    {
        var (code, mutate) = FailingCases[name];

        var report = ManifestValidator.Validate(Mutate(mutate));

        await Assert.That(report.Errors.Select(e => e.Code)).Contains(code);
        await Assert.That(report.ToResult()).Failed();
    }

    [Test]
    public async Task AllProblems_AreReported_NotJustTheFirst()
    {
        var report = ManifestValidator.Validate(Mutate(r =>
        {
            Step(r, "deploy-beta")["secrets"] = new JsonArray("NOPE");
            Step(r, "checkout")["input"]!["sha"] = Ref("input.shaa");
            r["triggers"]![1]!["intervalSeconds"] = 0;
        }));

        await Assert.That(report.Errors.Select(e => e.Code))
            .IsEquivalentTo([C.SecretUndeclared, C.RefPathUnknown, C.TriggerIntervalInvalid]);
        await Assert.That(report.ToResult().TryPickProblems(out var problems)).IsTrue();
        await Assert.That(problems!.Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Issue_CarriesCodeLocationAndStep()
    {
        var report = ManifestValidator.Validate(Mutate(r => Step(r, "deploy-beta")["secrets"] = new JsonArray("NOPE")));

        var issue = report.Errors.Single();
        await Assert.That(issue.Path).IsEqualTo("stages[2].steps[0].secrets[0]");
        await Assert.That(issue.Step).IsEqualTo("deploy-beta");
        var problem = issue.ToProblem();
        await Assert.That(problem.Tags).Contains(C.SecretUndeclared);
        await Assert.That(problem.Source).IsEqualTo("stages[2].steps[0].secrets[0]");
        await Assert.That(problem.ToBriefString()).Contains("NOPE");
    }

    [Test]
    public async Task UserTextWithBraces_DoesNotBreakProblemFormatting()
    {
        var report = ManifestValidator.Validate(Mutate(r => Step(r, "deploy-beta")["secrets"] = new JsonArray("{0}")));

        var messages = report.Errors.Select(e => e.ToProblem().ToBriefString()).ToList();
        await Assert.That(messages.Any(m => m.Contains("'{0}'"))).IsTrue();
    }

    [Test]
    public async Task AmbiguousProducers_ResolvedByAnExplicitRef_AreAccepted()
    {
        // Two producers of BuildArtifact; deploy-prod's whole ref picks one. In an explicit manifest this is
        // exactly what a disambiguating mapper emits, so it must not be rejected.
        var report = ManifestValidator.Validate(Mutate(r =>
        {
            var build2 = Step(r, "build").DeepClone();
            build2["name"] = "build-two";
            r["stages"]![2]!["steps"]!.AsArray().Add(build2);
        }));

        await Assert.That(report.Errors).IsEmpty();
        await Assert.That(report.Warnings.Select(w => w.Step)).Contains("build-two");
    }

    [Test]
    public async Task SameFailureHandler_AttachedTwice_IsAccepted()
    {
        var report = ManifestValidator.Validate(Mutate(r =>
        {
            var copy = r["failureHandlers"]![0]!.DeepClone();
            copy["steps"] = null;
            r["failureHandlers"]!.AsArray().Add(copy);
        }));

        await Assert.That(report.Errors).IsEmpty();
    }

    [Test]
    public async Task OptionalInput_FromDefinitelyAvailableProducer_IsAccepted()
    {
        var report = ManifestValidator.Validate(Mutate(r => Step(r, "summary")["inputTypes"]!["beta"]!["optional"] = true));

        await Assert.That(report.Errors).IsEmpty();
    }

    [Test]
    public async Task InsideWhen_EarlierInnerOutputs_AreDefinitelyAvailable()
    {
        // publish-cli requires deploy-prod (non-optional) — valid because both are inside the outer when.
        var report = ManifestValidator.Validate(Parse());

        await Assert.That(report.Errors.Where(e => e.Step == "publish-cli")).IsEmpty();
    }

    [Test]
    public async Task PlainInput_FromNullableAndOptionalFields_ResolvesThroughAnyOf()
    {
        var report = ManifestValidator.Validate(Mutate(r =>
        {
            r["models"]!["TestReport"]!["output"]!["properties"]!["note"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } },
                    new JsonObject { ["type"] = "null" }),
            };
            Step(r, "deploy-beta")["inputTypes"]!["note"] = StringSchema();
            Step(r, "deploy-beta")["input"]!["note"] = Ref("test.note.text");
        }));

        await Assert.That(report.Errors).IsEmpty();
    }

    [Test]
    public async Task ModelInput_RebuiltFieldByField_IsAccepted()
    {
        // Mirrors src/sdk/tests/compile-pass/mapper-shapes.ts: the model brand is optional on values.
        var report = ManifestValidator.Validate(Mutate(r => Step(r, "deploy-beta")["input"]!["artifact"] = new JsonObject
        {
            ["version"] = Ref("build.version"),
            ["commit"] = Ref("build.commit"),
            ["image"] = Ref("build.image"),
            ["chart"] = Ref("build.chart"),
        }));

        await Assert.That(report.Errors).IsEmpty();
    }

    [Test]
    public async Task ModelInput_RebuiltWithAMissingField_IsRejected()
    {
        var report = ManifestValidator.Validate(Mutate(r => Step(r, "deploy-beta")["input"]!["artifact"] = new JsonObject
        {
            ["version"] = Ref("build.version"),
            ["commit"] = Ref("build.commit"),
            ["image"] = Ref("build.image"),
        }));

        await Assert.That(report.Errors.Select(e => e.Code)).IsEquivalentTo([C.MappingFieldMissing]);
    }

    [Test]
    public async Task ModelInput_FromAStructurallyCompatibleField_IsAccepted()
    {
        var report = ManifestValidator.Validate(Mutate(r =>
        {
            r["models"]!["Commit"] = new JsonObject { ["output"] = r["models"]!["BuildArtifact"]!["output"]!["properties"]!["commit"]!.DeepClone() };
            Step(r, "deploy-beta")["inputTypes"]!["commit"] = ModelType("Commit");
            Step(r, "deploy-beta")["input"]!["commit"] = Ref("build.commit");
        }));

        await Assert.That(report.Errors).IsEmpty();
    }

    [Test]
    public async Task OptionalSourceField_IntoRequiredField_IsRejected()
    {
        var report = ManifestValidator.Validate(Mutate(r =>
        {
            r["models"]!["BuildArtifact"]!["output"]!["properties"]!["commit"]!["required"] = new JsonArray("repo");
            Step(r, "deploy-beta")["inputTypes"]!["commit"] = CommitSchema();
            Step(r, "deploy-beta")["input"]!["commit"] = Ref("build.commit");
        }));

        await Assert.That(report.Errors.Select(e => e.Code)).IsEquivalentTo([C.MappingFieldMissing]);
    }

    [Test]
    public async Task OptionalModelInput_FromAFieldOfAMaybeProducer_IsAccepted()
    {
        var report = ManifestValidator.Validate(Mutate(r =>
        {
            r["models"]!["Url"] = new JsonObject { ["output"] = new JsonObject { ["type"] = "string" } };
            Step(r, "summary")["inputTypes"]!["url"] = ModelType("Url", optional: true);
            Step(r, "summary")["input"]!["url"] = Ref("deploy-prod.url");
        }));

        await Assert.That(report.Errors).IsEmpty();
    }

    [Test]
    public async Task UnreferencedModel_IsAWarning()
    {
        var report = ManifestValidator.Validate(Mutate(r => r["models"]!["Spare"] = new JsonObject { ["output"] = new JsonObject { ["type"] = "string" } }));

        await Assert.That(report.Errors).IsEmpty();
        await Assert.That(report.Warnings.Select(w => w.Code)).Contains(C.ModelUnused);
    }

    [Test]
    public async Task EmptyWhen_IsAlways()
    {
        var report = ManifestValidator.Validate(Mutate(r => r["stages"]![3]!["when"] = new JsonObject()));

        await Assert.That(report.Errors).IsEmpty();
    }
}
