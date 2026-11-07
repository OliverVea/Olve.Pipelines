using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Manifests.ManifestFixture;
using C = Olve.Pipelines.Pipelines.Manifests.ManifestIssueCodes;

namespace Olve.Pipelines.UnitTests.Manifests;

public class ManifestValidatorTests
{
    [Test]
    public async Task SelfDeployFixture_IsValid_WithOnlyTheUnusedTestReportWarning()
    {
        var report = ManifestValidator.Validate(Parse());

        await Assert.That(report.Errors).IsEmpty();
        await Assert.That(report.Warnings.Select(w => (w.Code, w.Step))).IsEquivalentTo([(C.OutputUnused, (string?)"test")]);
        await Assert.That(report.ToResult()).Succeeded();
    }

    [Test]
    public async Task AMatrixStep_ReadThroughCellsInputs_IsValid()
    {
        var report = ManifestValidator.Validate(Mutate(r => MatrixBuild(r, Matrix(failure: "cascadeFailure", maxParallel: 1, exclude: [new JsonObject { ["os"] = "windows", ["arch"] = "arm64" }]))));

        await Assert.That(report.Errors).IsEmpty();
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

    // deploy-beta maps build.node, with these schemas under #/$defs/n0 on each side.
    private static Manifest NodeMapped(JsonObject source, JsonObject target) => Mutate(r =>
    {
        r["models"]!["Node"] = new JsonObject { ["output"] = new JsonObject { ["$ref"] = "#/$defs/n0", ["$defs"] = target } };
        var artifact = r["models"]!["BuildArtifact"]!["output"]!.AsObject();
        artifact["$defs"] = source;
        artifact["properties"]!["node"] = new JsonObject { ["$ref"] = "#/$defs/n0" };
        var deploy = Step(r, "deploy-beta");
        deploy["inputTypes"]!["artifact"] = ModelType("Node");
        deploy["input"]!["artifact"] = Ref("build.node");
    });

    private static JsonObject Fields(string next, int count, params string[] required)
    {
        var properties = new JsonObject();
        for (var i = 0; i < count; i++) properties[$"f{i}"] = new JsonObject { ["$ref"] = next };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => JsonValue.Create(r)).ToArray<JsonNode?>()),
        };
    }

    private static JsonObject Chain(int levels, int fields, JsonObject leaf)
    {
        var defs = new JsonObject { [$"n{levels}"] = leaf };
        for (var i = 0; i < levels; i++) defs[$"n{i}"] = Fields($"#/$defs/n{i + 1}", fields);
        return defs;
    }

    // Catches: paths multiplying per field, up to fields^16 checks and errors while reconcile holds the
    // binding's lock. The error count is bounded by the schema, not its paths, so a regression fails without a clock.
    [Test]
    public async Task ARecursiveSchema_IsCheckedOnce()
    {
        var source = new JsonObject { ["n0"] = Fields("#/$defs/n0", 2) };
        var target = new JsonObject { ["n0"] = Fields("#/$defs/n0", 2, "id") };

        var errors = ManifestValidator.Validate(NodeMapped(source, target)).Errors;

        await Assert.That(errors.Select(e => e.Message)).IsEquivalentTo(new[]
        {
            "step 'deploy-beta' input 'artifact': 'build.node' has no field 'id', which the input requires",
            "step 'deploy-beta' input 'artifact': 'build.node.f0' has no field 'id', which the input requires",
        });
    }

    [Test]
    public async Task ASchemaSharedByManyPaths_IsCheckedOnce()
    {
        var source = Chain(10, 2, new JsonObject { ["type"] = "string" });
        var target = Chain(10, 2, new JsonObject { ["type"] = "number" });

        var errors = ManifestValidator.Validate(NodeMapped(source, target)).Errors;

        await Assert.That(errors.Select(e => e.Code)).IsEquivalentTo(new[] { C.InputTypeMismatch });
    }

    // Catches: an unbounded walk down a long chain of $defs, which no schema repeats, so only the depth
    // limit stops it. A stack overflow can't be caught, and reconcile runs again at startup.
    [Test]
    public async Task ShapesAreCheckedToADepth()
    {
        var source = Chain(20, 1, new JsonObject { ["type"] = "string" });
        var target = Chain(20, 1, new JsonObject { ["type"] = "number" });

        await Assert.That(ManifestValidator.Validate(NodeMapped(source, target)).Errors).IsEmpty();
    }

    // Catches: a repeated field skipped as if both sides were shared, which would hide its error.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InlineTargetFields_EachReportTheirOwnError(bool sharedSource)
    {
        static JsonObject Pair(JsonObject field) => new()
        {
            ["n0"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["a"] = field.DeepClone(), ["b"] = field.DeepClone() },
            },
        };
        var source = Pair(sharedSource ? new JsonObject { ["$ref"] = "#/$defs/leaf" } : new JsonObject { ["type"] = "string" });
        source["leaf"] = new JsonObject { ["type"] = "string" };

        var errors = ManifestValidator.Validate(NodeMapped(source, Pair(new JsonObject { ["type"] = "number" }))).Errors;

        await Assert.That(errors.Count).IsEqualTo(2);
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
