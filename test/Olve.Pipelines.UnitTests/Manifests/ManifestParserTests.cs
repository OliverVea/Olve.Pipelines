using System.Text;
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

    private static readonly Dictionary<string, (string Code, Func<string> Json)> Malformed = new()
    {
        ["not json"] = (C.InvalidJson, () => "{\"apiVersion\": "),
        ["trailing comma"] = (C.InvalidJson, () => "{\"a\": 1,}"),
        ["too large"] = (C.TooLarge, () => "\"" + new string('x', ManifestParser.MaxBytes) + "\""),
        ["too deep"] = (C.InvalidJson, () => new string('[', ManifestParser.MaxDepth + 1) + new string(']', ManifestParser.MaxDepth + 1)),
        ["duplicate key"] = (C.DuplicateKey, () => Text.Replace("\"apiVersion\": \"2.0\",", "\"apiVersion\": \"2.0\", \"apiVersion\": \"2.0\",")),
        ["lone surrogate"] = (C.InvalidValue, () => Text.Replace("\"name\": \"Olve.Pipelines\"", "\"name\": \"\\ud800\"")),
        ["root not an object"] = (C.WrongType, () => "[]"),
        ["missing stages"] = (C.MissingProperty, () => Edit(r => r.Remove("stages"))),
        ["unknown property"] = (C.UnknownProperty, () => Edit(r => r["foo"] = 1)),
        ["unknown step property"] = (C.UnknownProperty, () => Edit(r => Step(r, "build")["cpu"] = 1)),
        ["name not a string"] = (C.WrongType, () => Edit(r => r["name"] = 5)),
        ["api version"] = (C.UnsupportedApiVersion, () => Edit(r => r["apiVersion"] = "3.0")),
        ["unknown trigger kind"] = (C.InvalidValue, () => Edit(r => r["triggers"]![0]!["kind"] = "cron")),
        ["push trigger renamed"] = (C.InvalidValue, () => Edit(r => r["triggers"]![0]!["name"] = "github")),
        ["poll without url"] = (C.MissingProperty, () => Edit(r => r["triggers"]![1]!.AsObject().Remove("url"))),
        ["fractional interval"] = (C.WrongType, () => Edit(r => r["triggers"]![1]!["intervalSeconds"] = 1.5)),
        ["empty stage"] = (C.StageEmpty, () => Edit(r => r["stages"]![0]!["steps"] = new JsonArray())),
        ["stage of unknown shape"] = (C.MissingProperty, () => Edit(r => r["stages"]!.AsArray().Add(new JsonObject { ["parallel"] = 1 }))),
        ["when without stages"] = (C.MissingProperty, () => Edit(r => r["stages"]![3]!.AsObject().Remove("stages"))),
        ["when with no values"] = (C.InvalidValue, () => Edit(r => r["stages"]![3]!["when"]!["deployTo"] = new JsonArray())),
        ["when with object value"] = (C.WrongType, () => Edit(r => r["stages"]![3]!["when"]!["deployTo"] = new JsonArray(new JsonObject()))),
        ["mapping not an object"] = (C.WrongType, () => Edit(r => Step(r, "checkout")["input"]!["sha"] = "input.sha")),
        ["input type of unknown shape"] = (C.MissingProperty, () => Edit(r => Step(r, "build")["inputTypes"]!["source"] = new JsonObject { ["type"] = "Source" })),
        ["output not a string"] = (C.WrongType, () => Edit(r => Step(r, "build")["output"] = 1)),
        ["model schema not an object"] = (C.WrongType, () => Edit(r => r["models"]!["Source"]!["output"] = true)),
        ["failure handler steps not array"] = (C.WrongType, () => Edit(r => r["failureHandlers"]![0]!["steps"] = "deploy-prod")),
        ["number out of range"] = (C.InvalidValue, () => Text.Replace("\"intervalSeconds\": 300", "\"intervalSeconds\": 1e400")),
    };

    private static string Edit(Action<JsonObject> edit)
    {
        var node = Node();
        edit(node);
        return node.ToJsonString();
    }

    public static IEnumerable<string> MalformedNames() => Malformed.Keys;

    [Test]
    [MethodDataSource(nameof(MalformedNames))]
    public async Task Malformed_IsRejectedWithACode(string name)
    {
        var (code, json) = Malformed[name];

        var ok = ManifestParser.TryParse(Encoding.UTF8.GetBytes(json()), out _, out var issues);

        await Assert.That(ok).IsFalse();
        await Assert.That(issues.Select(i => i.Code)).Contains(code);
        await Assert.That(ManifestParser.Parse(json())).Failed();
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
