using System.Text.Json.Nodes;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Commands.Dev;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Cli.Dev;

/// <summary>Argument parsing, pipeline-input assembly, step-input assembly and the local pool.</summary>
public class DevInputTests
{
    private static JsonObject Schema => DevFixtures.Manifest().InputSchema!;

    // ── argument parsing ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task CliArgs_RepeatedOption_CollectsAll_LastWinsForSingle()
    {
        CliArgs.Parse(["dev", "run", "x", "--input", "a=1", "--input=b=2"], new HashSet<string>(), new Dictionary<string, string>())
            .TryPickProblems(out _, out var cli);

        await Assert.That(string.Join(",", cli!.Options("input"))).IsEqualTo("a=1,b=2");
        await Assert.That(cli.Option("input")).IsEqualTo("b=2");
        await Assert.That(cli.Options("missing")).IsEmpty();
    }

    [Test]
    public async Task ParseFrom_AcceptsPipelineHashRun()
    {
        DevCommandSupport.ParseFrom("olve-pipelines#412").TryPickProblems(out _, out var from);
        await Assert.That(from.Pipeline).IsEqualTo("olve-pipelines");
        await Assert.That(from.Run).IsEqualTo(412);
    }

    [Test]
    [Arguments("olve-pipelines")]
    [Arguments("#4")]
    [Arguments("p#")]
    [Arguments("p#abc")]
    [Arguments("p#-1")]
    public async Task ParseFrom_RejectsMalformed(string value)
    {
        await Assert.That(DevCommandSupport.ParseFrom(value).Failed).IsTrue();
    }

    [Test]
    public async Task ParseSecrets_SplitsOnFirstEquals_AndRejectsMissingName()
    {
        DevCommandSupport.ParseSecrets(["TOKEN=a=b", "K="]).TryPickProblems(out _, out var secrets);
        await Assert.That(secrets!["TOKEN"]).IsEqualTo("a=b");
        await Assert.That(secrets["K"]).IsEqualTo("");
        await Assert.That(DevCommandSupport.ParseSecrets(["=x"]).Failed).IsTrue();
        await Assert.That(DevCommandSupport.ParseSecrets(["NOEQUALS"]).Failed).IsTrue();
    }

    [Test]
    public async Task ParseList_SplitsCommasAcrossRepeats()
    {
        await Assert.That(string.Join(",", DevCommandSupport.ParseList(["a,b", " c "])!)).IsEqualTo("a,b,c");
        await Assert.That(DevCommandSupport.ParseList([])).IsNull();
    }

    // ── pipeline input ────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PipelineInput_CoercesBySchemaType_AndFillsDefaults()
    {
        var result = PipelineInput.Build(Schema, null,
            ["sha=" + DevFixtures.Sha, "publish=true", "replicas=3", "labels={\"a\":1}"]);
        result.TryPickProblems(out _, out var input);

        await Assert.That(input!.ToJsonString()).IsEqualTo(
            $$"""{"sha":"{{DevFixtures.Sha}}","publish":true,"replicas":3,"labels":{"a":1},"deployTo":"beta"}""");
    }

    [Test]
    public async Task PipelineInput_LayersOverBase_KeepingEarlierValues()
    {
        var baseInput = new JsonObject { ["sha"] = "abc", ["deployTo"] = "prod" };
        PipelineInput.Build(Schema, baseInput, ["sha=def"]).TryPickProblems(out _, out var input);

        await Assert.That(input!["sha"]!.GetValue<string>()).IsEqualTo("def");
        await Assert.That(input["deployTo"]!.GetValue<string>()).IsEqualTo("prod");
        await Assert.That(baseInput["sha"]!.GetValue<string>()).IsEqualTo("abc"); // base not mutated
    }

    [Test]
    [Arguments("nope=1")]
    [Arguments("publish=yes")]
    [Arguments("replicas=two")]
    [Arguments("labels={bad")]
    [Arguments("noequals")]
    public async Task PipelineInput_RejectsBadPairs(string pair)
    {
        await Assert.That(PipelineInput.Build(Schema, null, [pair]).Failed).IsTrue();
    }

    [Test]
    public async Task PipelineInput_MissingRequired_ListsFields()
    {
        PipelineInput.Build(Schema, null, []).TryPickProblems(out _, out var input);
        await Assert.That(string.Join(",", PipelineInput.MissingRequired(Schema, input!))).IsEqualTo("sha");
    }

    // ── step input assembly ───────────────────────────────────────────────────────────────

    private static DevPool PoolWith(params (string Step, PoolEntry Entry)[] steps)
    {
        var pool = new DevPool { Input = new JsonObject { ["sha"] = DevFixtures.Sha, ["deployTo"] = "beta" } };
        foreach (var (step, entry) in steps)
            pool.Steps[step] = entry;
        return pool;
    }

    private static PoolEntry Ok(JsonNode output) => new(PoolEntry.Succeeded, output, null);

    [Test]
    public async Task Assemble_ResolvesInputRefs_ConstsAndNestedObjects()
    {
        var step = DevFixtures.Manifest().FindStep("src")!;
        var assembly = StepInputAssembler.Assemble(step, PoolWith());

        await Assert.That(assembly.Ready).IsTrue();
        await Assert.That(assembly.Input.ToJsonString()).IsEqualTo(
            $$$"""{"sha":"{{{DevFixtures.Sha}}}","opts":{"depth":1,"target":"beta"}}""");
    }

    [Test]
    public async Task Assemble_ResolvesStepRefsAndPaths_FromThePool()
    {
        var step = DevFixtures.Manifest().FindStep("build")!;
        var pool = PoolWith(("src", Ok(new JsonObject { ["sha"] = "abc", ["tree"] = "t" })));
        var assembly = StepInputAssembler.Assemble(step, pool);

        await Assert.That(assembly.Ready).IsTrue();
        await Assert.That(assembly.Input.ToJsonString()).IsEqualTo("""{"source":{"sha":"abc","tree":"t"},"commit":"abc"}""");
    }

    [Test]
    public async Task Assemble_UnsetPipelineInputField_IsAProblemWithHint()
    {
        var step = DevFixtures.Manifest().FindStep("src")!;
        var pool = new DevPool { Input = new JsonObject() };
        var assembly = StepInputAssembler.Assemble(step, pool);

        await Assert.That(assembly.Ready).IsFalse();
        await Assert.That(assembly.Problems.Any(p => p.Contains("--input sha="))).IsTrue();
    }

    [Test]
    public async Task Assemble_RequiredRootNeverRan_IsAProblemNamingTheStep()
    {
        var assembly = StepInputAssembler.Assemble(DevFixtures.Manifest().FindStep("build")!, PoolWith());

        await Assert.That(assembly.Problems.Count).IsEqualTo(2); // source + commit both need src
        await Assert.That(assembly.Problems[0]).Contains("pl dev run src");
    }

    [Test]
    public async Task Assemble_OptionalInput_OmittedWhenRootSkipped_RequiredIsChainedSkip()
    {
        var manifest = DevFixtures.Manifest();
        var pool = PoolWith(
            ("build", Ok(new JsonObject { ["v"] = 1 })),
            ("deploy", new PoolEntry(PoolEntry.Skipped, null, "when deployTo in [\"prod\"] did not match")));

        var summary = StepInputAssembler.Assemble(manifest.FindStep("summary")!, pool);
        await Assert.That(summary.Ready).IsTrue();
        await Assert.That(summary.Input.ToJsonString()).IsEqualTo("""{"artifact":{"v":1}}""");

        var notify = StepInputAssembler.Assemble(manifest.FindStep("notify")!, pool);
        await Assert.That(notify.Ready).IsFalse();
        await Assert.That(notify.Problems).IsEmpty();
        await Assert.That(notify.SkippedBecause.Single()).Contains("needs 'deploy', which was skipped");
    }

    [Test]
    public async Task Assemble_FailureRoot_AsksForInputFile()
    {
        var step = DevFixtures.Manifest().FindStep("triage")!;
        var assembly = StepInputAssembler.Assemble(step, PoolWith());

        await Assert.That(step.IsFailureHandler).IsTrue();
        await Assert.That(assembly.Problems.Single()).Contains("--input-file");
    }

    // ── pool ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Pool_RecordSucceeded_CopiesWorkFiles_AndRoundTrips()
    {
        using var temp = new TempPipelineDir();
        var ws = temp.Workspace;
        Directory.CreateDirectory(Path.Combine(ws.WorkFiles, "sub"));
        await File.WriteAllTextAsync(Path.Combine(ws.WorkFiles, "sub", "a.txt"), "hi");

        var pool = new DevPool { Input = new JsonObject { ["sha"] = "abc" } };
        pool.Record("build", new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["v"] = "1+x" } }, ws);
        pool.Record("lint", new JsonObject { ["status"] = "failed", ["error"] = new JsonObject { ["kind"] = "handler", ["message"] = "boom" } }, ws);
        pool.Record("gate", new JsonObject { ["status"] = "skipped", ["reason"] = "nothing to do" }, ws);
        pool.Save(ws.PoolJson);

        await Assert.That(File.Exists(Path.Combine(ws.PoolFilesFor("build"), "sub", "a.txt"))).IsTrue();
        await Assert.That(ws.PoolFilesFor("build")).EndsWith(Path.Combine("files", "0", "build", "1", "files"));

        DevPool.Load(ws.PoolJson).TryPickProblems(out _, out var loaded);
        await Assert.That(loaded!.Input!["sha"]!.GetValue<string>()).IsEqualTo("abc");
        await Assert.That(loaded.Steps["build"].Output!["v"]!.GetValue<string>()).IsEqualTo("1+x");
        await Assert.That(loaded.Steps["lint"].Status).IsEqualTo(PoolEntry.Failed);
        await Assert.That(loaded.Steps["lint"].Reason).IsEqualTo("boom");
        await Assert.That(loaded.Steps["gate"].Reason).IsEqualTo("nothing to do");
        await Assert.That(await File.ReadAllTextAsync(ws.PoolJson)).Contains("1+x"); // not +
    }

    [Test]
    public async Task Pool_MissingFile_LoadsEmpty_CorruptFileFails()
    {
        using var temp = new TempPipelineDir();
        DevPool.Load(temp.Workspace.PoolJson).TryPickProblems(out _, out var empty);
        await Assert.That(empty!.Steps).IsEmpty();

        Directory.CreateDirectory(temp.Workspace.Root);
        await File.WriteAllTextAsync(temp.Workspace.PoolJson, "{nope");
        await Assert.That(DevPool.Load(temp.Workspace.PoolJson).Failed).IsTrue();
    }
}
