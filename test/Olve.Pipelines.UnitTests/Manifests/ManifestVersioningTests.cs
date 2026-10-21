using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using static Olve.Pipelines.UnitTests.Manifests.ManifestFixture;

namespace Olve.Pipelines.UnitTests.Manifests;

public class ManifestVersioningTests
{
    private static readonly string[] AllStepNames =
        ["checkout", "build", "test", "deploy-beta", "deploy-prod", "publish-cli", "summary", "aoe-triage"];

    private static Dictionary<string, int> StepVersions(ManifestVersions v) => v.Current.Steps.ToDictionary(kv => kv.Key, kv => kv.Value.Version);

    private static Dictionary<string, int> ModelVersions(ManifestVersions v) => v.Current.Models.ToDictionary(kv => kv.Key, kv => kv.Value.Version);

    [Test]
    public async Task FirstVersion_NumbersEverythingOne()
    {
        var v = ManifestVersioning.Compute(null, Parse());

        await Assert.That(v.Current.Steps.Keys).IsEquivalentTo(AllStepNames);
        await Assert.That(v.Current.Steps.Values.All(e => e.Version == 1)).IsTrue();
        await Assert.That(v.Current.Models.Count).IsEqualTo(8);
        await Assert.That(v.Current.Models.Values.All(e => e.Version == 1)).IsTrue();
        await Assert.That(v.ChangedSteps).IsEquivalentTo(AllStepNames);
        await Assert.That(v.ManifestHash).IsEqualTo(ManifestVersioning.ManifestHash(Parse()));
        await Assert.That(v.CanonicalManifest).IsEqualTo(Text);
        await Assert.That(v.Current.Steps["build"].ContentHash).Matches("^sha256:[0-9a-f]{64}$");
    }

    [Test]
    public async Task Unchanged_KeepsAllNumbers()
    {
        var v1 = ManifestVersioning.Compute(null, Parse());

        var v2 = ManifestVersioning.Compute(v1.History, Parse());

        await Assert.That(v2.Current).IsEquivalentTo(v1.Current);
        await Assert.That(v2.ChangedSteps).IsEmpty();
        await Assert.That(v2.ChangedModels).IsEmpty();
        await Assert.That(v2.ManifestHash).IsEqualTo(v1.ManifestHash);
    }

    [Test]
    public async Task HandlerChange_BumpsOnlyThatStep()
    {
        var v1 = ManifestVersioning.Compute(null, Parse());

        var v2 = ManifestVersioning.Compute(v1.History,
            Mutate(r => Step(r, "deploy-beta")["handlerHash"] = "sha256:" + new string('1', 64)));

        await Assert.That(v2.ChangedSteps).IsEquivalentTo(["deploy-beta"]);
        await Assert.That(StepVersions(v2)["deploy-beta"]).IsEqualTo(2);
        await Assert.That(StepVersions(v2)["deploy-prod"]).IsEqualTo(1);
        await Assert.That(v2.ChangedModels).IsEmpty();
        await Assert.That(v2.ManifestHash).IsNotEqualTo(v1.ManifestHash);
    }

    [Test]
    [Arguments("image")]
    [Arguments("secrets")]
    [Arguments("timeout")]
    [Arguments("retries")]
    [Arguments("mapping")]
    [Arguments("config")]
    [Arguments("kind")]
    public async Task EachStepContentField_BumpsTheStep(string field)
    {
        var v1 = ManifestVersioning.Compute(null, Parse());
        Action<JsonObject> mutate = field switch
        {
            "image" => r => Step(r, "test")["image"] = "mcr.microsoft.com/dotnet/sdk:11.0",
            "secrets" => r => Step(r, "test")["secrets"] = new JsonArray("GITHUB_TOKEN"),
            "timeout" => r => Step(r, "test")["timeoutSeconds"] = 60,
            "retries" => r => Step(r, "test")["retries"] = 2,
            "mapping" => r => Step(r, "test")["input"]!["source"] = Ref("checkout.tree"),
            "config" => r => Step(r, "test")["config"]!["env"] = new JsonObject { ["A"] = "1" },
            "kind" => r => Step(r, "test")["kind"] = "custom",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var v2 = ManifestVersioning.Compute(v1.History, Mutate(mutate));

        await Assert.That(v2.ChangedSteps).IsEquivalentTo(["test"]);
        await Assert.That(StepVersions(v2)["test"]).IsEqualTo(2);
    }

    [Test]
    public async Task ModelChange_RipplesToEveryStepUsingIt()
    {
        var v1 = ManifestVersioning.Compute(null, Parse());

        var v2 = ManifestVersioning.Compute(v1.History, Mutate(r =>
            r["models"]!["BuildArtifact"]!["output"]!["properties"]!["digest"] = new JsonObject { ["type"] = "string" }));

        await Assert.That(v2.ChangedModels).IsEquivalentTo(["BuildArtifact"]);
        await Assert.That(ModelVersions(v2)["BuildArtifact"]).IsEqualTo(2);
        // producer (output) and consumers (inputs) all bump; unrelated steps keep their numbers
        await Assert.That(v2.ChangedSteps).IsEquivalentTo(["build", "deploy-beta", "deploy-prod", "publish-cli"]);
        await Assert.That(StepVersions(v2)["checkout"]).IsEqualTo(1);
        await Assert.That(StepVersions(v2)["summary"]).IsEqualTo(1);
    }

    [Test]
    public async Task ModelChangedBack_StillIncrements()
    {
        var v1 = ManifestVersioning.Compute(null, Parse());
        var v2 = ManifestVersioning.Compute(v1.History, Mutate(r => r["models"]!["CliRelease"]!["output"]!["properties"]!["x"] = new JsonObject()));

        var v3 = ManifestVersioning.Compute(v2.History, Parse());

        await Assert.That(ModelVersions(v3)["CliRelease"]).IsEqualTo(3);
        await Assert.That(StepVersions(v3)["publish-cli"]).IsEqualTo(3);
        await Assert.That(StepVersions(v3)["summary"]).IsEqualTo(3); // optional(CliRelease) input
    }

    [Test]
    public async Task WhenAndPlacementChanges_AreNotStepChanges()
    {
        var v1 = ManifestVersioning.Compute(null, Parse());

        var v2 = ManifestVersioning.Compute(v1.History, Mutate(r =>
        {
            r["stages"]![3]!["when"] = new JsonObject { ["deployTo"] = new JsonArray("beta", "prod") };
            var stages = r["stages"]!.AsArray();
            var testStep = Step(r, "test").DeepClone();
            stages[1]!["steps"]!.AsArray().RemoveAt(1);
            stages.Insert(2, new JsonObject { ["steps"] = new JsonArray(testStep) });
        }));

        await Assert.That(v2.ChangedSteps).IsEmpty();
        await Assert.That(v2.ChangedModels).IsEmpty();
        await Assert.That(v2.ManifestHash).IsNotEqualTo(v1.ManifestHash);
    }

    [Test]
    public async Task SecretOrder_IsNotAChange()
    {
        var v1 = ManifestVersioning.Compute(null, Mutate(r => Step(r, "test")["secrets"] = new JsonArray("GITHUB_TOKEN", "SSH_PRIVATE_KEY")));

        var v2 = ManifestVersioning.Compute(v1.History, Mutate(r => Step(r, "test")["secrets"] = new JsonArray("SSH_PRIVATE_KEY", "GITHUB_TOKEN")));

        await Assert.That(v2.ChangedSteps).IsEmpty();
    }

    [Test]
    public async Task RemovedStep_StaysInHistory_AndReAddingUnchangedResumesItsNumber()
    {
        var withTestTimeout = (Action<JsonObject>)(r => Step(r, "test")["timeoutSeconds"] = 60);
        var v1 = ManifestVersioning.Compute(null, Parse());
        var v2 = ManifestVersioning.Compute(v1.History, Mutate(withTestTimeout)); // test.2

        var v3 = ManifestVersioning.Compute(v2.History, Mutate(r => r["stages"]![1]!["steps"]!.AsArray().RemoveAt(1)));

        await Assert.That(v3.Current.Steps.ContainsKey("test")).IsFalse();
        await Assert.That(v3.History.Steps["test"].Version).IsEqualTo(2); // retired, history kept
        await Assert.That(v3.History.Models.ContainsKey("TestReport")).IsTrue();

        var v4 = ManifestVersioning.Compute(v3.History, Mutate(withTestTimeout));
        await Assert.That(StepVersions(v4)["test"]).IsEqualTo(2);
        await Assert.That(v4.ChangedSteps).IsEmpty();
    }

    [Test]
    public async Task ReAddedStepWithChanges_ContinuesNumbering()
    {
        var v1 = ManifestVersioning.Compute(null, Parse());
        var v2 = ManifestVersioning.Compute(v1.History, Mutate(r => r["stages"]![1]!["steps"]!.AsArray().RemoveAt(1)));

        var v3 = ManifestVersioning.Compute(v2.History, Mutate(r => Step(r, "test")["image"] = "alpine:3"));

        await Assert.That(StepVersions(v3)["test"]).IsEqualTo(2);
        await Assert.That(v3.ChangedSteps).IsEquivalentTo(["test"]);
    }

    [Test]
    public async Task Rename_IsANewIdentity()
    {
        var v1 = ManifestVersioning.Compute(null, Parse());

        var v2 = ManifestVersioning.Compute(v1.History, Mutate(r => Step(r, "test")["name"] = "unit-test"));

        await Assert.That(StepVersions(v2)["unit-test"]).IsEqualTo(1);
        await Assert.That(v2.Current.Steps.ContainsKey("test")).IsFalse();
        await Assert.That(v2.History.Steps["test"].Version).IsEqualTo(1);
    }

    [Test]
    public async Task EntityId_IsNameDotVersion()
    {
        await Assert.That(ManifestVersioning.EntityId("deploy-beta", 3)).IsEqualTo("deploy-beta.3");
    }
}
