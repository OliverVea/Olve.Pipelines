using System.Text.Json.Nodes;
using Olve.Pipelines.Cli.Commands.Dev;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Cli.Dev;

/// <summary>`pl dev` with a config: it picks each `select` case, reaches the step as `$config`, and names the case in OLVE_CASES.</summary>
public class DevConfigTests
{
    // `deploy` is in both cases of a select on config `target.kind`, with a different image per case.
    private const string ManifestJson = """
    {
      "apiVersion": "2.0",
      "name": "demo",
      "description": "d",
      "input": "Req",
      "models": {
        "Req": {
          "input": { "type": "object", "properties": { "sha": { "type": "string" } }, "required": ["sha"] },
          "output": { "type": "object", "properties": { "sha": { "type": "string" } }, "required": ["sha"] }
        }
      },
      "secrets": [],
      "triggers": [],
      "stages": [
        { "steps": [ { "name": "build", "kind": "custom", "image": "build-img", "output": null, "secrets": [], "input": {}, "inputTypes": {} } ] },
        { "select": "target", "discriminator": "kind", "cases": {
            "ssh": [ { "steps": [ { "name": "deploy", "kind": "helmDeployOverSsh", "image": "ssh-img", "output": null, "secrets": [], "input": {}, "inputTypes": {} } ] } ],
            "inCluster": [ { "steps": [ { "name": "deploy", "kind": "helmDeployInCluster", "image": "cluster-img", "output": null, "secrets": [], "input": {}, "inputTypes": {} } ] } ]
        } },
        { "steps": [ { "name": "summary", "kind": "custom", "image": "build-img", "output": null, "secrets": [], "input": {}, "inputTypes": {} } ] }
      ],
      "failureHandlers": []
    }
    """;

    private static readonly JsonObject Ssh = new() { ["target"] = new JsonObject { ["kind"] = "ssh", ["host"] = "box" } };

    private static DevManifest Manifest() => TestRunDomain.Pick(DevManifest.Parse(ManifestJson));

    [Test]
    public async Task Flatten_WithAConfig_KeepsOnlyTheSelectedCase_AndRecordsIt()
    {
        var plan = DevPlan.Flatten(Manifest(), Ssh);

        await Assert.That(string.Join(",", plan.Select(p => $"{p.Step.Name}:{p.Step.Image}"))).IsEqualTo("build:build-img,deploy:ssh-img,summary:build-img");
        await Assert.That(plan.Single(p => p.Step.Name == "deploy").Cases).IsEquivalentTo(["target.kind=ssh"]);
        await Assert.That(plan.Single(p => p.Step.Name == "build").Cases).IsEmpty();
        await Assert.That(DevPlan.Flatten(Manifest()).Count(p => p.Step.Name == "deploy")).IsEqualTo(2);
    }

    // Catches: `pl dev run deploy` running some case's variant (or failing in the harness) without saying why.
    [Test]
    public async Task PlaceStep_AStepSeveralCasesShare_NeedsAConfig()
    {
        await Assert.That(DevCommandSupport.PlaceStep(Manifest(), "deploy", null)).Failed();

        var placed = TestRunDomain.Pick(DevCommandSupport.PlaceStep(Manifest(), "deploy", Ssh));
        await Assert.That(placed.Step.Image).IsEqualTo("ssh-img");
        await Assert.That(placed.Cases).IsEquivalentTo(["target.kind=ssh"]);
        await Assert.That(DevCommandSupport.PlaceStep(Manifest(), "nope", Ssh)).Failed();
    }

    // Catches: `pl dev pipeline` failing on every step that reads config, or on any step inside a select.
    [Test]
    public async Task Pipeline_WithAConfig_GivesEachStepItAndTheSelectedCase()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(ManifestJson);
        var session = new DevSession(harness, DevFixtures.Tools, temp.Dir, temp.Workspace, new DevConsole(false, TextWriter.Null, TextWriter.Null));
        var pool = TestRunDomain.Pick(DevPool.Load(temp.Workspace.PoolJson));
        var options = new DevPipelineOptions { InputPairs = ["sha=abc"], Yes = true, Run = new DevRunOptions { Config = Ssh, WarnUndeclaredSecrets = false } };

        var run = await DevPipelineCommand.RunAsync(new DevContext(session, Manifest(), pool), options, new FakePrompt(ConfirmAnswer.Yes),
            new DevConsole(false, TextWriter.Null, TextWriter.Null), [], CancellationToken.None);

        await Assert.That(run).Succeeded();
        await Assert.That(string.Join(",", harness.RanSteps)).IsEqualTo("build,deploy,summary");
        await Assert.That(harness.Inputs["deploy"]["$config"]!.ToJsonString()).IsEqualTo(Ssh.ToJsonString());
        var deploy = harness.Calls.Single(c => c.Arguments.Count > 3 && c.Arguments[3] == "deploy");
        await Assert.That(deploy.Environment[DevSession.CasesEnvVar]).IsEqualTo("""["target.kind=ssh"]""");
        var build = harness.Calls.Single(c => c.Arguments.Count > 3 && c.Arguments[3] == "build");
        await Assert.That(build.Environment[DevSession.CasesEnvVar]).IsNull();
    }

    [Test]
    public async Task ConfigOption_ReadsAJsonFile_AndRejectsBadInput()
    {
        using var temp = new TempPipelineDir();
        var good = Path.Combine(temp.Dir, "config.json");
        var bad = Path.Combine(temp.Dir, "config.yaml");
        File.WriteAllText(good, Ssh.ToJsonString());
        File.WriteAllText(bad, "target:\n  kind: ssh\n");
        var flags = new HashSet<string>(StringComparer.Ordinal);

        var read = TestRunDomain.Pick(await DevCommandSupport.ResolveConfigAsync(DevFixtures.Args(flags, "dev", "run", "x", "--config", good), DevFixtures.Context(), CancellationToken.None));

        await Assert.That(read!.ToJsonString()).IsEqualTo(Ssh.ToJsonString());
        await Assert.That(await DevCommandSupport.ResolveConfigAsync(DevFixtures.Args(flags, "dev", "run", "x", "--config", bad), DevFixtures.Context(), CancellationToken.None)).Failed();
        await Assert.That(await DevCommandSupport.ResolveConfigAsync(
            DevFixtures.Args(flags, "dev", "run", "x", "--config", good, "--config-from", "p"), DevFixtures.Context(), CancellationToken.None)).Failed();
        await Assert.That(await DevCommandSupport.ResolveConfigAsync(DevFixtures.Args(flags, "dev", "run", "x"), DevFixtures.Context(), CancellationToken.None))
            .SucceededAndValue(v => v.IsNull());
    }
}
