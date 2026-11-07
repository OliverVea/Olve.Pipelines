using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Manifests.ManifestFixture;
using C = Olve.Pipelines.Pipelines.Manifests.ManifestIssueCodes;

namespace Olve.Pipelines.UnitTests.Manifests;

public class ManifestParserTests
{
    [Test]
    public async Task SelfDeployFixture_Parses()
    {
        var result = ManifestParser.Parse(Bytes);

        await Assert.That(result).Succeeded();
        result.TryPickProblems(out _, out var m);
        await Assert.That(m!.Name).IsEqualTo("Olve.Pipelines");
        await Assert.That(m.Input).IsEqualTo("DeployRequest");
        await Assert.That(m.Harness.Bun).IsEqualTo("1.4.3");
        await Assert.That(m.Models.Count).IsEqualTo(8);
        await Assert.That(m.Models["DeployRequest"].Input).IsNotNull();
        await Assert.That(m.Models["Source"].Input).IsNull();
        await Assert.That(m.Secrets.Select(s => s.Name)).IsEquivalentTo(["GITHUB_TOKEN", "SSH_PRIVATE_KEY"]);
        await Assert.That(m.Triggers[0]).IsTypeOf<PushTrigger>();
        var poll = (PollTrigger)m.Triggers[1];
        await Assert.That(poll.Headers["Authorization"]).IsEqualTo("$SECRET:GITHUB_TOKEN");
        await Assert.That(poll.IntervalSeconds).IsEqualTo(300);
        await Assert.That(m.Stages.Count).IsEqualTo(5);
        var when = (WhenStage)m.Stages[3];
        await Assert.That(when.When["deployTo"].Single().GetString()).IsEqualTo("prod");
        var nested = (WhenStage)when.Stages[1];
        await Assert.That(nested.When["publishCli"].Single().GetBoolean()).IsTrue();
        var checkout = ((StepsStage)m.Stages[0]).Steps.Single();
        await Assert.That(checkout.InputTypes["repo"]).IsTypeOf<SchemaInputType>();
        await Assert.That(((RefMapping)checkout.Input["sha"]).Ref).IsEqualTo("input.sha");
        var test = ((StepsStage)m.Stages[1]).Steps[1];
        await Assert.That(test.Kind).IsEqualTo(StepKinds.Script);
        await Assert.That(test.Config!["scriptSha256"].GetString()).StartsWith("sha256:");
        var build = ((StepsStage)m.Stages[1]).Steps[0];
        await Assert.That(build.TimeoutSeconds).IsEqualTo(1800);
        var summary = ((StepsStage)m.Stages[4]).Steps.Single();
        await Assert.That(summary.Output).IsNull();
        await Assert.That(((ModelInputType)summary.InputTypes["cli"]).Optional).IsTrue();
        await Assert.That(m.FailureHandlers.Single().Steps).IsEquivalentTo(["deploy-prod", "deploy-beta"]);
    }

    [Test]
    public async Task Mapping_DiscriminatesRefConstAndNestedObjects()
    {
        var m = Mutate(r => Step(r, "checkout")["input"]!["sha"] = new JsonObject
        {
            ["a"] = Ref("input.sha"),
            ["b"] = Const(new JsonObject { ["ref"] = "not-a-ref" }),
            ["c"] = new JsonObject { ["d"] = Const(null) },
        });

        var mapping = (ObjectMapping)((StepsStage)m.Stages[0]).Steps[0].Input["sha"];
        await Assert.That(mapping.Fields["a"]).IsTypeOf<RefMapping>();
        var b = (ConstMapping)mapping.Fields["b"];
        await Assert.That(b.Value.GetProperty("ref").GetString()).IsEqualTo("not-a-ref");
        var c = (ObjectMapping)mapping.Fields["c"];
        await Assert.That(((ConstMapping)c.Fields["d"]).Value.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
    }

    [Test]
    public async Task Retention_IsReadWhenPresent_AndNullOtherwise()
    {
        await Assert.That(Parse().Retention).IsNull();

        var m = Mutate(r => r["retention"] = new JsonObject { ["keepLatestRuns"] = 5, ["buildGraceDays"] = 0 });

        await Assert.That(m.Retention).IsEqualTo(new ManifestRetention(5, null, 0));
    }

    [Test]
    public async Task Retention_SurvivesTheWriter_AndChangesTheManifestHash()
    {
        var without = Parse();
        var with = Mutate(r => r["retention"] = new JsonObject { ["keepLatestRuns"] = 50, ["minRunAgeDays"] = 30 });

        var written = ManifestWriter.ToCanonicalJson(with);
        var reparsed = ManifestParser.Parse(Encoding.UTF8.GetBytes(written));

        await Assert.That(reparsed).Succeeded();
        await Assert.That(TestRunDomain.Pick(reparsed).Retention).IsEqualTo(new ManifestRetention(50, 30, null));
        await Assert.That(ManifestVersioning.ManifestHash(with)).IsNotEqualTo(ManifestVersioning.ManifestHash(without));
        await Assert.That(ManifestWriter.ToCanonicalJson(without)).DoesNotContain("\"retention\"");
    }

    [Test]
    public async Task Matrix_IsRead_AndTheWriterReproducesDescribesBytes()
    {
        var node = Node();
        MatrixBuild(node, Matrix(failure: "cascadeFailure", maxParallel: 2, exclude: [new JsonObject { ["os"] = "windows", ["arch"] = "arm64" }]));
        var bare = Node();
        MatrixBuild(bare);

        var m = ParseNode(node);
        var build = ((StepsStage)m.Stages[1]).Steps[0];
        var reader = ((StepsStage)m.Stages[2]).Steps[0];

        await Assert.That(build.Matrix!.Axes.Select(a => a.Name)).IsEquivalentTo(["os", "arch"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(build.Matrix.Exclude!.Single()["arch"]).IsEqualTo("arm64");
        await Assert.That(build.Matrix.Cascades).IsTrue();
        await Assert.That(build.Matrix.MaxParallel).IsEqualTo(2);
        await Assert.That(((ModelInputType)reader.InputTypes["artifact"]).Cells).IsTrue();
        foreach (var json in new[] { node, bare })
        {
            var canonical = CanonicalJson.Serialize(JsonDocument.Parse(json.ToJsonString()).RootElement);
            await Assert.That(ManifestWriter.ToCanonicalJson(ParseNode(json))).IsEqualTo(canonical);
        }
    }

    [Test]
    public async Task StepDescription_IsRead_AndMissingReadsAsEmpty()
    {
        var m = Mutate(r => Step(r, "build").Remove("description"));

        var steps = ((StepsStage)m.Stages[0]).Steps.Concat(((StepsStage)m.Stages[1]).Steps).ToDictionary(s => s.Name);
        await Assert.That(steps["checkout"].Description).IsEqualTo("Fetches the repository at the pushed commit.");
        await Assert.That(steps["build"].Description).IsEqualTo("");
    }

    [Test]
    public async Task RefMapping_SplitsRootAndPath()
    {
        var r = new RefMapping("build.commit.sha");

        await Assert.That(r.Root).IsEqualTo("build");
        await Assert.That(r.Path).IsEquivalentTo(["commit", "sha"]);
        await Assert.That(new RefMapping("build").Path).IsEmpty();
    }

    [Test]
    public async Task Utf8Bom_IsAccepted()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes).ToArray();

        await Assert.That(ManifestParser.Parse(withBom)).Succeeded();
    }

    [Test]
    public async Task ShapeProblems_AreAllReported_WithPaths()
    {
        var json = Edit(r =>
        {
            r["name"] = 5;
            Step(r, "build")["cpu"] = 1;
            r["triggers"]![1]!.AsObject().Remove("url");
        });

        ManifestParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var issues);

        await Assert.That(issues.Select(i => i.Path))
            .IsEquivalentTo(["name", "stages[1].steps[0].cpu", "triggers[1].url"]);
    }
}
