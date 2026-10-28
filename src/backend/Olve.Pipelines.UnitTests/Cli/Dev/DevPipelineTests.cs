using System.Text.Json.Nodes;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Dev;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Cli.Dev;

/// <summary>Stage flattening, `when` evaluation, selection, pool chaining, confirmation, and the commands.</summary>
public class DevPipelineTests
{
    // ── plan + when ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Flatten_NumbersStages_AndAccumulatesNestedWhen()
    {
        var plan = DevPlan.Flatten(DevFixtures.Manifest());

        await Assert.That(string.Join(",", plan.Select(p => $"{p.Stage}:{p.Step.Name}")))
            .IsEqualTo("1:src,2:build,2:lint,3:deploy,4:publish,5:summary,6:notify");
        await Assert.That(string.Join(",", plan.Single(p => p.Step.Name == "publish").Conditions.Select(c => c.Field)))
            .IsEqualTo("deployTo,publish");
        await Assert.That(plan.Any(p => p.Step.Name == "triage")).IsFalse(); // failure handlers aren't stages
    }

    [Test]
    public async Task Flatten_RealSelfDeployManifest()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Manifests", "Fixtures", "self-deploy.manifest.json"));
        DevManifest.Parse(text).TryPickProblems(out _, out var manifest);
        var plan = DevPlan.Flatten(manifest!);

        await Assert.That(plan.First().Step.Name).IsEqualTo("checkout");
        await Assert.That(plan.Single(p => p.Step.Name == "deploy-prod").Conditions.Single().ToString()).IsEqualTo("deployTo in [\"prod\"]");
        await Assert.That(manifest!.FindStep("aoe-triage")!.IsFailureHandler).IsTrue();
    }

    [Test]
    public async Task Describe_ShowsWhatAStepWaitsFor()
    {
        var output = new StringWriter();
        DevDescribeCommand.Render(output, DevFixtures.Manifest());

        await Assert.That(output.ToString()).Contains("publish              alpine  [secrets: TOKEN]  [waits for: verdict (Verdict)]");
        await Assert.That(output.ToString()).DoesNotContain("deploy               alpine -> Release  [secrets: SSH_KEY]  [waits");
    }

    [Test]
    public async Task WhenMismatch_MatchesAnyValue_AllConditions()
    {
        var conditions = DevPlan.Flatten(DevFixtures.Manifest()).Single(p => p.Step.Name == "publish").Conditions;

        await Assert.That(DevPlan.WhenMismatch(conditions, new JsonObject { ["deployTo"] = "prod", ["publish"] = true })).IsNull();
        await Assert.That(DevPlan.WhenMismatch(conditions, new JsonObject { ["deployTo"] = "beta", ["publish"] = true }))
            .IsEqualTo("when deployTo in [\"prod\"] did not match (deployTo = \"beta\")");
        await Assert.That(DevPlan.WhenMismatch(conditions, new JsonObject { ["deployTo"] = "prod" }))
            .IsEqualTo("when publish in [true] did not match (publish = unset)");
        await Assert.That(DevPlan.WhenMismatch(conditions, new JsonObject { ["deployTo"] = "prod", ["publish"] = "true" }))
            .IsNotNull(); // JSON equality: the string "true" is not true
    }

    [Test]
    public async Task Select_UntilRunsThroughThatStage_OnlyFilters_UnknownFails()
    {
        var plan = DevPlan.Flatten(DevFixtures.Manifest());

        DevPlan.Select(plan, "build", null).TryPickProblems(out _, out var until);
        await Assert.That(string.Join(",", until!.Select(p => p.Step.Name))).IsEqualTo("src,build,lint");

        DevPlan.Select(plan, null, ["summary", "src"]).TryPickProblems(out _, out var only);
        await Assert.That(string.Join(",", only!.Select(p => p.Step.Name))).IsEqualTo("src,summary");

        await Assert.That(DevPlan.Select(plan, "nope", null).Failed).IsTrue();
        await Assert.That(DevPlan.Select(plan, null, ["src", "nope"]).Failed).IsTrue();
    }

    // ── dev pipeline (fake harness) ───────────────────────────────────────────────────────

    private static (DevContext Dev, FakeHarness Harness, StringWriter Out, StringWriter Err) Open(TempPipelineDir temp, bool json = false)
    {
        var harness = new FakeHarness(DevFixtures.ManifestJson);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var session = new DevSession(harness, DevFixtures.Tools, temp.Dir, temp.Workspace, new DevConsole(json, stdout, stderr));
        DevPool.Load(temp.Workspace.PoolJson).TryPickProblems(out _, out var pool);
        return (new DevContext(session, DevFixtures.Manifest(), pool!), harness, stdout, stderr);
    }

    private static Task<Result<int>> Run(DevContext dev, IConfirmPrompt prompt, List<PipelineStepResult> results, DevPipelineOptions options) =>
        DevPipelineCommand.RunAsync(dev, options, prompt, new DevConsole(false, TextWriter.Null, TextWriter.Null), results, CancellationToken.None);

    private static readonly string[] BaseInput = ["sha=" + DevFixtures.Sha];

    [Test]
    public async Task Pipeline_Beta_ChainsPool_SkipsWhenBlock_AndPropagatesSkips()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);
        harness.Steps["src"] = (input, olveDir) =>
        {
            Directory.CreateDirectory(Path.Combine(olveDir, "work", "files"));
            File.WriteAllText(Path.Combine(olveDir, "work", "files", "tree.tar"), "t");
            return (new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["sha"] = input["sha"]!.DeepClone() } }, 0);
        };
        var results = new List<PipelineStepResult>();

        var run = await Run(dev, new FakePrompt(ConfirmAnswer.Yes), results, new DevPipelineOptions { InputPairs = BaseInput, Yes = true });

        await Assert.That(run.TryPickProblems(out _, out var exitCode)).IsFalse();
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(string.Join(",", harness.RanSteps)).IsEqualTo("src,build,lint,summary");
        // build got src's output (pool chaining), and the pipeline input with defaults
        await Assert.That(harness.Inputs["build"].ToJsonString()).IsEqualTo($$"""{"source":{"sha":"{{DevFixtures.Sha}}"},"commit":"{{DevFixtures.Sha}}"}""");
        await Assert.That(harness.Inputs["src"]["opts"]!["target"]!.GetValue<string>()).IsEqualTo("beta");
        // summary's optional `release` is left out; notify (required) is skipped with a chained reason
        await Assert.That(harness.Inputs["summary"].ContainsKey("release")).IsFalse();
        var notify = results.Single(r => r.Step == "notify");
        await Assert.That(notify.Status).IsEqualTo("skipped");
        await Assert.That(notify.Detail!).Contains("needs 'deploy', which was skipped (when deployTo in [\"prod\"]");
        await Assert.That(results.Single(r => r.Step == "publish").Status).IsEqualTo("skipped");
        // files chained into the pool tree, run env points the harness at it
        await Assert.That(File.Exists(Path.Combine(temp.Workspace.PoolFilesFor("src"), "tree.tar"))).IsTrue();
        var buildCall = harness.Calls.Single(c => c.Arguments.Count > 3 && c.Arguments[3] == "build");
        await Assert.That(buildCall.Environment["OLVE_FILES_DIR"]).IsEqualTo(temp.Workspace.PoolFiles);
        await Assert.That(buildCall.Environment["OLVE_DIR"]).IsEqualTo(temp.Workspace.Root);
        await Assert.That(buildCall.Environment.ContainsKey("OLVE_RESULT_URL") && buildCall.Environment["OLVE_RESULT_URL"] is null).IsTrue();
    }

    [Test]
    public async Task Pipeline_Prod_RunsWhenBlockSteps()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);
        var results = new List<PipelineStepResult>();

        var run = await Run(dev, new FakePrompt(ConfirmAnswer.Yes), results,
            new DevPipelineOptions { InputPairs = [.. BaseInput, "deployTo=prod", "publish=true"], Yes = true });

        await Assert.That(run.Succeeded).IsTrue();
        await Assert.That(string.Join(",", harness.RanSteps)).IsEqualTo("src,build,lint,deploy,publish,summary,notify");
        await Assert.That(harness.Inputs["summary"]["release"]!["from"]!.GetValue<string>()).IsEqualTo("deploy");
    }

    [Test]
    public async Task Pipeline_MissingRequiredInput_FailsBeforeRunning()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);

        var run = await Run(dev, new FakePrompt(ConfirmAnswer.Yes), [], new DevPipelineOptions { Yes = true });

        await Assert.That(run.Failed).IsTrue();
        await Assert.That(harness.RanSteps).IsEmpty();
    }

    [Test]
    public async Task Pipeline_SecretStep_Prompts_YesRuns()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);
        var prompt = new FakePrompt(ConfirmAnswer.Yes);

        var run = await Run(dev, prompt, [], new DevPipelineOptions { InputPairs = [.. BaseInput, "deployTo=prod"] });

        await Assert.That(run.Succeeded).IsTrue();
        // src (TOKEN) and deploy (SSH_KEY) declare secrets; publish is skipped (publish=false)
        await Assert.That(prompt.Questions.Count).IsEqualTo(2);
        await Assert.That(prompt.Questions[0]).Contains("'src' declares secrets (TOKEN)");
        await Assert.That(harness.RanSteps).Contains("deploy");
    }

    [Test]
    public async Task Pipeline_SecretStep_DeclinedStops()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);
        var results = new List<PipelineStepResult>();

        var run = await Run(dev, new FakePrompt(ConfirmAnswer.No), results, new DevPipelineOptions { InputPairs = BaseInput });

        await Assert.That(run.Failed).IsTrue();
        await Assert.That(harness.RanSteps).IsEmpty();
        await Assert.That(results.Single().Status).IsEqualTo("declined");
    }

    [Test]
    public async Task Pipeline_SecretStep_NoTerminal_RefusesWithoutYes()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);

        var run = await Run(dev, new FakePrompt(ConfirmAnswer.NotInteractive), [], new DevPipelineOptions { InputPairs = BaseInput });

        await Assert.That(run.TryPickProblems(out var problems)).IsTrue();
        await Assert.That(problems!.First().ToString()).Contains("--yes");
        await Assert.That(harness.RanSteps).IsEmpty();
    }

    [Test]
    public async Task Pipeline_Yes_NeverPrompts()
    {
        using var temp = new TempPipelineDir();
        var (dev, _, _, _) = Open(temp);
        var prompt = new FakePrompt(ConfirmAnswer.No);

        var run = await Run(dev, prompt, [], new DevPipelineOptions { InputPairs = BaseInput, Yes = true });

        await Assert.That(run.Succeeded).IsTrue();
        await Assert.That(prompt.Questions).IsEmpty();
    }

    [Test]
    public async Task Pipeline_FailedStep_StopsWithHarnessExitCode()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);
        harness.Steps["build"] = (_, _) =>
            (new JsonObject { ["status"] = "failed", ["error"] = new JsonObject { ["kind"] = "output", ["message"] = "bad" } }, 66);
        var results = new List<PipelineStepResult>();

        var run = await Run(dev, new FakePrompt(ConfirmAnswer.Yes), results, new DevPipelineOptions { InputPairs = BaseInput, Yes = true });

        run.TryPickProblems(out _, out var exitCode);
        await Assert.That(exitCode).IsEqualTo(66);
        await Assert.That(string.Join(",", harness.RanSteps)).IsEqualTo("src,build");
        await Assert.That(results.Last().Detail).IsEqualTo("bad");
    }

    [Test]
    public async Task Pipeline_Only_ReusesPool_FullRunResetsIt()
    {
        using var temp = new TempPipelineDir();
        var (dev, harness, _, _) = Open(temp);
        _ = await Run(dev, new FakePrompt(ConfirmAnswer.Yes), [], new DevPipelineOptions { InputPairs = BaseInput, Until = "build", Yes = true });

        // a fresh process: the pool comes back from pool.json
        var (dev2, harness2, _, _) = Open(temp);
        var run = await Run(dev2, new FakePrompt(ConfirmAnswer.Yes), [], new DevPipelineOptions { Only = ["summary"], Yes = true });

        await Assert.That(run.Succeeded).IsTrue();
        await Assert.That(string.Join(",", harness2.RanSteps)).IsEqualTo("summary");
        await Assert.That(harness2.Inputs["summary"]["artifact"]!["from"]!.GetValue<string>()).IsEqualTo("build");

        // a full run starts fresh: without --input sha the stored sha is gone
        var (dev3, _, _, _) = Open(temp);
        var full = await Run(dev3, new FakePrompt(ConfirmAnswer.Yes), [], new DevPipelineOptions { Yes = true });
        await Assert.That(full.Failed).IsTrue();
        await Assert.That(string.Join(",", harness.RanSteps)).IsEqualTo("src,build,lint");
    }

    // ── secrets env ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task SecretEnvironment_OnlyDeclaredSecrets_OverridesWin()
    {
        using var temp = new TempPipelineDir();
        var (dev, _, _, err) = Open(temp);
        var deploy = dev.Manifest.FindStep("deploy")!;

        var env = dev.Session.SecretEnvironment(dev.Manifest, deploy, new Dictionary<string, string> { ["SSH_KEY"] = "k", ["TOKEN"] = "t" });

        await Assert.That(env["SSH_KEY"]).IsEqualTo("k");
        await Assert.That(env.ContainsKey("TOKEN") && env["TOKEN"] is null).IsTrue(); // undeclared: removed from the child env
        await Assert.That(err.ToString()).Contains("--secret TOKEN ignored");
    }

    // ── commands ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DevRun_From_IsParsedThenRefused()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(DevFixtures.ManifestJson);
        var command = new DevRunCommand(harness, DevFixtures.Tools, new DevConsole(false, TextWriter.Null, TextWriter.Null));

        var ok = await command.Execute(DevFixtures.Args(command.BooleanFlags, "dev", "run", "src", "--dir", temp.Dir, "--from", "demo#7"), DevFixtures.Context(), CancellationToken.None);
        await Assert.That(ok.TryPickProblems(out var problems)).IsTrue();
        await Assert.That(problems!.First().ToString()).Contains("not available until the v2 run API lands");

        var bad = await command.Execute(DevFixtures.Args(command.BooleanFlags, "dev", "run", "src", "--dir", temp.Dir, "--from", "demo"), DevFixtures.Context(), CancellationToken.None);
        await Assert.That(bad.TryPickProblems(out var badProblems)).IsTrue();
        await Assert.That(badProblems!.First().ToString()).Contains("<pipeline>#<run>");
        await Assert.That(harness.Calls).IsEmpty();
    }

    [Test]
    public async Task DevRun_MirrorsHarnessExitCode_AndPrintsEnvelope()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(DevFixtures.ManifestJson);
        harness.Steps["src"] = (_, _) =>
            (new JsonObject { ["status"] = "failed", ["error"] = new JsonObject { ["kind"] = "input", ["message"] = "sha: bad" } }, 65);
        var stdout = new StringWriter();
        var command = new DevRunCommand(harness, DevFixtures.Tools, new DevConsole(false, stdout, TextWriter.Null));
        var ctx = DevFixtures.Context();

        var result = await command.Execute(
            DevFixtures.Args(command.BooleanFlags, "dev", "run", "src", "--dir", temp.Dir, "--input", "sha=" + DevFixtures.Sha, "--secret", "TOKEN=t"),
            ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(ctx.ExitCode).IsEqualTo(65);
        await Assert.That(stdout.ToString()).Contains("src: failed (input, exit 65)");
        await Assert.That(stdout.ToString()).Contains("log from src");
        await Assert.That(stdout.ToString()).DoesNotContain(DevSession.ResultBegin); // sentinel block filtered
        var runCall = harness.Calls.Last();
        await Assert.That(runCall.Environment["TOKEN"]).IsEqualTo("t");
        await Assert.That(runCall.Environment["SSH_KEY"]).IsNull();
        // the pool remembers the input; the failure is recorded
        DevPool.Load(temp.Workspace.PoolJson).TryPickProblems(out _, out var pool);
        await Assert.That(pool!.Input!["sha"]!.GetValue<string>()).IsEqualTo(DevFixtures.Sha);
        await Assert.That(pool.Steps["src"].Status).IsEqualTo(PoolEntry.Failed);
    }

    [Test]
    public async Task DevRun_SuccessiveRuns_Chain_JsonPrintsRawEnvelope()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(DevFixtures.ManifestJson);
        var stdout = new StringWriter();
        var command = new DevRunCommand(harness, DevFixtures.Tools, new DevConsole(true, stdout, TextWriter.Null));

        _ = await command.Execute(DevFixtures.Args(command.BooleanFlags, "dev", "run", "src", "--dir", temp.Dir, "--input", "sha=" + DevFixtures.Sha), DevFixtures.Context(true), CancellationToken.None);
        stdout.GetStringBuilder().Clear();
        var ctx = DevFixtures.Context(true);
        var result = await command.Execute(DevFixtures.Args(command.BooleanFlags, "dev", "run", "build", "--dir", temp.Dir), ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(ctx.ExitCode).IsEqualTo(0);
        await Assert.That(harness.Inputs["build"]["source"]!["from"]!.GetValue<string>()).IsEqualTo("src");
        await Assert.That(stdout.ToString().Trim()).IsEqualTo("""{"status":"succeeded","output":{"from":"build"}}""");
    }

    [Test]
    public async Task DevRun_InputFile_IsUsedVerbatim()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(DevFixtures.ManifestJson);
        var file = Path.Combine(temp.Dir, "in.json");
        await File.WriteAllTextAsync(file, """{"failure":{"step":"deploy"}}""");
        var command = new DevRunCommand(harness, DevFixtures.Tools, new DevConsole(false, TextWriter.Null, TextWriter.Null));

        var result = await command.Execute(DevFixtures.Args(command.BooleanFlags, "dev", "run", "triage", "--dir", temp.Dir, "--input-file", file), DevFixtures.Context(), CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(harness.Inputs["triage"]["failure"]!["step"]!.GetValue<string>()).IsEqualTo("deploy");
    }

    [Test]
    public async Task DevRun_UnknownStep_AndMissingPipelineTs_Fail()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(DevFixtures.ManifestJson);
        var command = new DevRunCommand(harness, DevFixtures.Tools, new DevConsole(false, TextWriter.Null, TextWriter.Null));

        var unknown = await command.Execute(DevFixtures.Args(command.BooleanFlags, "dev", "run", "nope", "--dir", temp.Dir), DevFixtures.Context(), CancellationToken.None);
        await Assert.That(unknown.Failed).IsTrue();

        var noDir = await command.Execute(DevFixtures.Args(command.BooleanFlags, "dev", "run", "src", "--dir", Path.Combine(temp.Dir, "missing")), DevFixtures.Context(), CancellationToken.None);
        await Assert.That(noDir.TryPickProblems(out var problems)).IsTrue();
        await Assert.That(problems!.First().ToString()).Contains("No pipeline.ts");
    }

    [Test]
    public async Task DevDescribe_PrintsSummary_OrRawJson()
    {
        using var temp = new TempPipelineDir();
        var stdout = new StringWriter();
        var command = new DevDescribeCommand(new FakeHarness(DevFixtures.ManifestJson), DevFixtures.Tools, new DevConsole(false, stdout, TextWriter.Null));

        var result = await command.Execute(DevFixtures.Args(CliCommand.NoFlags, "dev", "describe", "--dir", temp.Dir), DevFixtures.Context(), CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        var text = stdout.ToString();
        await Assert.That(text).Contains("demo — A demo pipeline.");
        await Assert.That(text).Contains("sha*");
        await Assert.That(text).Contains("when deployTo in [\"prod\"]:");
        await Assert.That(text).Contains("[secrets: SSH_KEY]");
        await Assert.That(text).Contains("triage");

        var json = new StringWriter();
        var jsonCommand = new DevDescribeCommand(new FakeHarness(DevFixtures.ManifestJson), DevFixtures.Tools, new DevConsole(true, json, TextWriter.Null));
        _ = await jsonCommand.Execute(DevFixtures.Args(CliCommand.NoFlags, "dev", "describe", "--dir", temp.Dir), DevFixtures.Context(true), CancellationToken.None);
        await Assert.That(json.ToString()).IsEqualTo(DevFixtures.ManifestJson);
    }

    [Test]
    public async Task DevDescribe_Problems_ExitWithDescribeCode()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(DevFixtures.ManifestJson) { DescribeExitCode = 78, DescribeStderr = "duplicate step name 'build'" };
        var stderr = new StringWriter();
        var command = new DevDescribeCommand(harness, DevFixtures.Tools, new DevConsole(false, TextWriter.Null, stderr));
        var ctx = DevFixtures.Context();

        var result = await command.Execute(DevFixtures.Args(CliCommand.NoFlags, "dev", "describe", "--dir", temp.Dir), ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(ctx.ExitCode).IsEqualTo(78);
        await Assert.That(stderr.ToString()).Contains("duplicate step name 'build'");
    }

    [Test]
    public async Task Dispatcher_DevCommands_DontUseTheApi()
    {
        ICliCommand[] commands =
        [
            new DevDescribeCommand(new FakeHarness("")),
            new DevRunCommand(new FakeHarness("")),
            new DevPipelineCommand(new FakeHarness(""), new FakePrompt(ConfirmAnswer.No)),
        ];
        foreach (var command in commands)
        {
            await Assert.That(command.UsesApi).IsFalse();
            await Assert.That(command.Noun).IsEqualTo("dev");
        }
    }
}
