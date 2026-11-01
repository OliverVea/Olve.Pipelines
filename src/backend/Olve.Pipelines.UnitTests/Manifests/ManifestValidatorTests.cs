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

    // Catches: a self-referencing model overflowing the stack, which can't be caught: one push would
    // crash-loop the service, because reconcile runs again at startup. Regressed, this ends the test process.
    [Test]
    public async Task RecursiveModels_AreCheckedToADepth_InsteadOfForever()
    {
        static JsonObject Node(string self) => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["next"] = new JsonObject { ["$ref"] = self } },
        };
        var manifest = Mutate(r =>
        {
            r["models"]!["Node"] = new JsonObject { ["output"] = Node("#") };
            var artifact = r["models"]!["BuildArtifact"]!["output"]!.AsObject();
            artifact["$defs"] = new JsonObject { ["node"] = Node("#/$defs/node") };
            artifact["properties"]!["node"] = new JsonObject { ["$ref"] = "#/$defs/node" };
            var deploy = Step(r, "deploy-beta");
            deploy["inputTypes"]!["artifact"] = ModelType("Node");
            deploy["input"]!["artifact"] = Ref("build.node");
        });

        await Assert.That(ManifestValidator.Validate(manifest).Errors).IsEmpty();
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
