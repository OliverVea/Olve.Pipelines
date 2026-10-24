using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Results.TUnit;
using static Olve.Pipelines.UnitTests.Configs.ConfigManifests;

namespace Olve.Pipelines.UnitTests.Configs;

/// <summary><c>select</c>: a branch on config. The fixture selects on <c>target</c> (ssh | inCluster); both cases have a step <c>count</c>.</summary>
public class SelectTests
{
    private const string UnionTarget = """
        {"oneOf":[
          {"type":"object","properties":{"kind":{"type":"string","const":"ssh"},"host":{"type":"string"}},"required":["kind","host"]},
          {"type":"object","properties":{"kind":{"type":"string","const":"inCluster"}},"required":["kind"]}
        ]}
        """;

    private static JsonObject Variant(JsonNode count, string kind, string condition, bool clusterAccess)
    {
        var step = count.DeepClone().AsObject();
        step["kind"] = kind;
        step["config"] = new JsonObject { ["target"] = new JsonObject { ["$config"] = "target", ["$case"] = condition } };
        step["configEntries"] = new JsonArray("target");
        if (clusterAccess) step["clusterAccess"] = true;
        return step;
    }

    /// <summary>The config fixture with <c>target</c> a union and stage 1's <c>count</c> replaced by a select with one variant per kind.</summary>
    private static JsonObject Node(Action<JsonObject>? change = null)
    {
        var node = JsonNode.Parse(Text())!.AsObject();
        node["config"]![1]!["schema"] = JsonNode.Parse(UnionTarget);
        var stage = node["stages"]![1]!["steps"]!.AsArray();
        var count = stage.First(s => s!["name"]!.GetValue<string>() == "count")!;
        stage.Remove(count);
        var select = new JsonObject
        {
            ["select"] = "target",
            ["discriminator"] = "kind",
            ["cases"] = new JsonObject
            {
                ["ssh"] = new JsonArray(new JsonObject { ["steps"] = new JsonArray(Variant(count, StepKinds.HelmDeployOverSsh, "target.kind=ssh", false)) }),
                ["inCluster"] = new JsonArray(new JsonObject { ["steps"] = new JsonArray(Variant(count, StepKinds.HelmDeployInCluster, "target.kind=inCluster", true)) }),
            },
        };
        node["stages"]!.AsArray().Insert(1, select);
        change?.Invoke(node);
        return node;
    }

    private static Manifest Manifest(Action<JsonObject>? change = null) => Parse(Node(change).ToJsonString());

    private static JsonElement Config(string target) => JsonDocument.Parse("{\"target\":" + target + ",\"auth\":{\"mode\":\"none\"}}").RootElement.Clone();

    [Test]
    public async Task Select_Parses_RoundTrips_AndValidates_WithTheSameNameInBothCases()
    {
        var manifest = Manifest();
        var written = ManifestWriter.ToCanonicalJson(manifest);

        await Assert.That(manifest.Stages[1]).IsTypeOf<SelectStage>();
        await Assert.That(ManifestWriter.ToCanonicalJson(Parse(written))).IsEqualTo(written);
        await Assert.That(ManifestValidator.Validate(manifest).Errors).IsEmpty();
    }

    [Test]
    public async Task SameName_OutsideExclusiveCases_IsADuplicate()
    {
        var manifest = Manifest(n =>
        {
            var again = n["stages"]![1]!["cases"]!["ssh"]![0]!["steps"]![0]!.DeepClone();
            again.AsObject().Remove("config");
            again.AsObject().Remove("configEntries");
            n["stages"]!.AsArray().Insert(3, new JsonObject { ["steps"] = new JsonArray(again) });
        });

        await Assert.That(ManifestValidator.Validate(manifest).Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.StepNameDuplicate);
    }

    [Test]
    public async Task MissingCase_AndANarrowedReferenceOutsideItsCase_AreErrors()
    {
        var missing = Manifest(n => n["stages"]![1]!["cases"]!.AsObject().Remove("inCluster"));
        var leaked = Manifest(n => n["stages"]![1]!["cases"]!["ssh"]![0]!["steps"]![0]!["config"]!["target"]!["$case"] = "target.kind=inCluster");

        await Assert.That(ManifestValidator.Validate(missing).Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.SelectCaseMissing);
        await Assert.That(ManifestValidator.Validate(leaked).Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.ConfigRefOutsideCase);
    }

    [Test]
    public async Task Versioning_HashesAName_OverAllItsVariants()
    {
        var before = ManifestVersioning.Compute(null, Manifest());
        var changed = ManifestVersioning.Compute(before.History,
            Manifest(n => n["stages"]![1]!["cases"]!["inCluster"]![0]!["steps"]![0]!["image"] = "alpine:3.23"));

        await Assert.That(before.Current.Steps["count"].Version).IsEqualTo(1);
        await Assert.That(changed.ChangedSteps).IsEquivalentTo(["count"]);
        await Assert.That(changed.Current.Steps["count"].Version).IsEqualTo(2);
    }

    [Test]
    public async Task EffectivePlan_HasOnlyTheSelectedCase_AndItsConditions()
    {
        var manifest = Manifest();

        var ssh = TestRunDomain.Pick(RunPlan.ForConfig(manifest, Config("""{"kind":"ssh","host":"h"}""")));
        var inCluster = TestRunDomain.Pick(RunPlan.ForConfig(manifest, Config("""{"kind":"inCluster"}""")));

        await Assert.That(ssh.TryGetStep("count", out var stage, out var step)).IsTrue();
        await Assert.That(step!.Kind).IsEqualTo(StepKinds.HelmDeployOverSsh);
        await Assert.That(stage!.Cases).IsEquivalentTo(["target.kind=ssh"]);
        await Assert.That(ssh.Selected["target"]).IsEqualTo("ssh");
        await Assert.That(inCluster.TryGetStep("count", out _, out var other)).IsTrue();
        await Assert.That(other!.Kind).IsEqualTo(StepKinds.HelmDeployInCluster);
        await Assert.That(RunPlan.ForConfig(manifest, Config("""{"kind":"docker"}"""))).Failed();
        await Assert.That(RunPlan.From(manifest).TryGetStep("count", out _, out _)).IsFalse();
    }

    [Test]
    public async Task Gate_AsksForClusterAccess_OnlyWhenTheSelectedBranchNeedsIt()
    {
        var manifest = Manifest();
        var inCluster = Yaml("target: {kind: inCluster}\nauth: {mode: none}\n");
        var ssh = Yaml("target: {kind: ssh, host: h}\nauth: {mode: none}\n");

        var refused = ConfigValidation.Check(manifest, 9, inCluster, new HashSet<string>(), grantsClusterAccess: false);
        var granted = ConfigValidation.Check(manifest, 9, inCluster, new HashSet<string>(), grantsClusterAccess: true);
        var overSsh = ConfigValidation.Check(manifest, 9, ssh, new HashSet<string>(), grantsClusterAccess: false);

        await Assert.That(refused.Satisfied).IsFalse();
        await Assert.That(refused.Reasons).Contains("step count needs cluster access, but the instance grants no step ServiceAccount (Kubernetes:StepServiceAccount)");
        await Assert.That(granted.Satisfied).IsTrue();
        await Assert.That(overSsh.Satisfied).IsTrue();
    }

    [Test]
    public async Task StepJob_InACase_GetsItsConditions_AsOlveCases()
    {
        var request = new HarnessJobRequest("olve-aaaaaaaabbbbccccddddeeeeeeeeeeee", Harness.HarnessJobBuilderTests.AttemptId, Harness.HarnessJobBuilderTests.PipelineId, Harness.HarnessJobBuilderTests.RunId,
            1, "count", 1, "2026.10.24.1", "alpine:3.22", 300, [], "olve-pipeline-x",
            new HarnessUrls("http://garage/step", null, "http://garage/result", new Dictionary<string, string>())) { Cases = ["target.kind=ssh"] };

        var job = HarnessJobBuilder.Build(request, new HarnessOptions()).Value!.Job;
        var env = job.Spec.Template.Spec.Containers[0].Env!;

        await Assert.That(env.Single(e => e.Name == HarnessJobBuilder.CasesKey).Value).IsEqualTo("""["target.kind=ssh"]""");
    }
}
