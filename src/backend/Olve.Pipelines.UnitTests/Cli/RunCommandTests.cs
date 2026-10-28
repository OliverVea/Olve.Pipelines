using System.Text.Json;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Dev;
using Olve.Pipelines.Cli.Commands.Pipelines;
using Olve.Pipelines.Cli.Commands.Runs;
using Olve.Pipelines.Cli.Commands.Steps;
using Olve.Pipelines.Cli.Commands.Wait;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;
using Olve.Pipelines.UnitTests.Cli.Dev;
using Olve.Results;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Cli;

/// <summary>Plumbing of the v2 run/step/version commands over <see cref="FakePipelinesApi"/>.</summary>
public class RunCommandTests
{
    private const string Id = "11111111-1111-1111-1111-111111111111";
    private const string Name = "v2 test fixture";

    private static FakePipelinesApi Api() => new()
    {
        ListPipelinesResult = Result.Success<Pipeline[]>([new Pipeline { Id = Guid.Parse(Id), Name = Name }]),
    };

    private static (CommandContext Ctx, StringWriter Out, CliArgs Cli) Build(
        ICliCommand command, FakePipelinesApi api, params string[] args)
    {
        var json = args.Contains("--json");
        var booleans = new HashSet<string>(command.BooleanFlags, StringComparer.Ordinal) { "json", "verbose" };
        CliArgs.Parse(args, booleans, command.Aliases).TryPickProblems(out _, out var cli);
        cli!.OperandOffset = string.IsNullOrEmpty(command.Verb) ? 1 : 2;

        var stdout = new StringWriter();
        var ctx = new CommandContext
        {
            Json = json,
            Verbose = false,
            Api = api,
            Output = new ConsoleOutputWriter(json, stdout),
            Log = new StderrLog(false, TextWriter.Null),
            Config = new CliConfig(),
        };
        return (ctx, stdout, cli);
    }

    private static async Task<(Result Result, string Out)> Run(ICliCommand command, FakePipelinesApi api, params string[] args)
    {
        var (ctx, stdout, cli) = Build(command, api, args);
        var result = await command.Execute(cli, ctx, CancellationToken.None);
        return (result, stdout.ToString());
    }

    [Test]
    [Arguments(false, "#16  Succeeded", 0)]
    [Arguments(true, "#14  Superseded", 1)]
    public async Task WaitRun_FollowsSupersededRuns_UnlessNoFollow(bool noFollow, string expected, int exitCode)
    {
        static RunDetail Detail(int number, string status, int? next) =>
            new() { Run = new RunSummary { Number = number, Status = status, SupersededByNumber = next } };
        var api = Api();
        api.GetRunByNumber = n => n switch
        {
            14 => Detail(14, "Superseded", 15),
            15 => Detail(15, "Superseded", 16),
            _ => Detail(16, "Succeeded", null),
        };
        string[] args = noFollow ? ["wait", "run", $"{Name}#14", "--no-follow"] : ["wait", "run", $"{Name}#14"];
        var (ctx, stdout, cli) = Build(new WaitRunCommand(), api, args);

        await Assert.That(await new WaitRunCommand().Execute(cli, ctx, CancellationToken.None)).Succeeded();
        await Assert.That(stdout.ToString()).Contains(expected);
        await Assert.That(ctx.ExitCode ?? 0).IsEqualTo(exitCode);
    }

    private const string Manifest = """
        { "name": "fixture", "input": "In",
          "models": { "In": { "input": { "type": "object", "properties": { "sha": { "type": "string" }, "mode": { "type": "string" } } } } },
          "secrets": [], "triggers": [], "stages": [] }
        """;

    private static void WithActiveVersion(FakePipelinesApi api)
    {
        api.ListPipelineVersionsResult = new PipelineVersionsResponse
        {
            Activation = new PipelineActivation { Type = "active", VersionNumber = 4 },
            Versions = [new PipelineVersionSummary { Number = 4, CommitSha = "dbdb4b02dc1f" }],
        };
        api.GetPipelineVersionResult = new PipelineVersion { Number = 4, Manifest = Manifest, ManifestHash = "sha256:x" };
    }

    [Test]
    public async Task Trigger_ByName_TypesFieldsWithTheActiveVersion_AndPrintsTheAddress()
    {
        var api = Api();
        WithActiveVersion(api);
        api.TriggerRunResult = new TriggerRunResponse { Outcome = "started", Run = new Run { Number = 15, VersionNumber = 4, VersionStamp = "2026.10.21.15" } };

        var (result, output) = await Run(new TriggerCommand(), api, "trigger", Name, "--field", "sha=1234567", "--field", "mode=normal");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastPipelineId).IsEqualTo(Id);
        await Assert.That(api.LastBody).IsEqualTo("""{"sha":"1234567","mode":"normal"}""");
        await Assert.That(api.Calls).Contains("GetPipelineVersion(4)");
        await Assert.That(output.Split('\n')[0].Trim()).IsEqualTo("v2 test fixture#15");
    }

    [Test]
    public async Task Trigger_WithoutFields_SkipsTheSchemaLookup_AndReportsQueued()
    {
        var api = Api();
        api.TriggerRunResult = new TriggerRunResponse { Outcome = "queued" };

        var (result, output) = await Run(new TriggerCommand(), api, "trigger", Id, "--input", """{"sha":"abc"}""");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastBody).IsEqualTo("""{"sha":"abc"}""");
        await Assert.That(api.Invoked("ListPipelineVersions")).IsFalse();
        await Assert.That(output).Contains("queued");
    }

    [Test]
    public async Task Trigger_ServerProblems_AreReturned()
    {
        var api = Api();
        api.TriggerRunResult = new ResultProblemCollection([new ResultProblem("input.sha: is required")]);

        var (result, _) = await Run(new TriggerCommand(), api, "trigger", Id);

        await Assert.That(result.TryPickProblems(out var problems)).IsTrue();
        await Assert.That(problems!.First().ToString()).Contains("input.sha");
    }

    private static RunDetail HeldRun() => new()
    {
        Run = new RunSummary { Number = 15, Status = "Running", TriggerKind = "Manual", VersionNumber = 4, HeldAt = "last" },
        CommitSha = "dbdb4b02dc1f",
        Input = JsonDocument.Parse("""{"mode":"normal"}""").RootElement.Clone(),
        Continuable = true,
        Stages =
        [
            new RunStageView { Index = 0, Steps = [new RunStepView { Name = "hello", Status = "succeeded", AttemptCount = 2, LatestAttempt = 2, SucceededAttempt = 1, Reason = "boom" }] },
            new RunStageView
            {
                Index = 1,
                When = [new RunStageCondition { Field = "mode", Values = [JsonDocument.Parse("\"normal\"").RootElement.Clone()] }],
                Steps = [new RunStepView { Name = "last", Status = "held", Blocked = true }],
            },
        ],
    };

    [Test]
    public async Task RunGet_RendersStagesWhenHeldAndBlocked()
    {
        var api = Api();
        api.GetRunResult = HeldRun();

        var (result, output) = await Run(new RunGetCommand(), api, "run", "get", $"{Name}#15");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastRun).IsEqualTo(15);
        await Assert.That(output).Contains("v2 test fixture#15  Running");
        await Assert.That(output).Contains("held at last");
        await Assert.That(output).Contains("stage 2  when mode in [\"normal\"]");
        await Assert.That(output).Contains("blocked");
        await Assert.That(output).Contains("uses @1");
        await Assert.That(output).Contains("commit dbdb4b0");
    }

    [Test]
    public async Task RunGet_Json_EmitsTheDetail()
    {
        var api = Api();
        api.GetRunResult = HeldRun();

        var (_, output) = await Run(new RunGetCommand(), api, "run", "get", $"{Id}#15", "--json");

        using var doc = JsonDocument.Parse(output);
        await Assert.That(doc.RootElement.GetProperty("stages")[1].GetProperty("steps")[0].GetProperty("blocked").GetBoolean()).IsTrue();
        await Assert.That(doc.RootElement.GetProperty("input").GetProperty("mode").GetString()).IsEqualTo("normal");
    }

    [Test]
    public async Task RunGet_RejectsAStepAddress()
    {
        var (result, _) = await Run(new RunGetCommand(), Api(), "run", "get", $"{Name}#15/hello");

        await Assert.That(result.Succeeded).IsFalse();
    }

    [Test]
    public async Task RunContinue_SendsOnlyTheChangedFields_TypedByThePinnedVersion()
    {
        var api = Api();
        WithActiveVersion(api);
        api.GetRunResult = HeldRun();
        api.ContinueRunResult = new Run { Number = 15, Status = "Running", Input = """{"mode":"skip-last"}""" };

        var (result, _) = await Run(new RunContinueCommand(), api, "run", "continue", $"{Name}#15", "--field", "mode=skip-last");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastBody).IsEqualTo("""{"mode":"skip-last"}""");
        await Assert.That(api.Calls).Contains("GetPipelineVersion(4)");
    }

    [Test]
    public async Task RunContinue_WithoutChanges_SendsNoInput()
    {
        var api = Api();
        api.ContinueRunResult = new Run { Number = 15, Status = "Running" };

        var (result, _) = await Run(new RunContinueCommand(), api, "run", "continue", $"{Name}#15");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastBody).IsNull();
    }

    [Test]
    public async Task StepRerun_PassesContinue_AndRejectsAnAttempt()
    {
        var api = Api();
        api.RerunStepResult = new Attempt { RunNumber = 15, StepName = "echo", Number = 2 };

        var (ok, output) = await Run(new StepRerunCommand(), api, "step", "rerun", $"{Name}#15/echo", "--continue");
        var (withAttempt, _) = await Run(new StepRerunCommand(), api, "step", "rerun", $"{Name}#15/echo@1");

        await Assert.That(ok.Succeeded).IsTrue();
        await Assert.That(api.LastFlag == true).IsTrue();
        await Assert.That(api.LastStep).IsEqualTo("echo");
        await Assert.That(output).Contains("v2 test fixture#15/echo@2");
        await Assert.That(withAttempt.Succeeded).IsFalse();
    }

    [Test]
    public async Task StepOutput_PassesTheAttempt_AndPrintsTheOutputJson()
    {
        var api = Api();
        api.GetStepOutputResult = new AttemptOutputResponse
        {
            Run = 15,
            Step = "hello",
            Attempt = 1,
            Status = "succeeded",
            Output = JsonDocument.Parse("""{"message":"hi"}""").RootElement.Clone(),
        };

        var (result, output) = await Run(new StepOutputCommand(), api, "step", "output", $"{Name}#15/hello@1");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastAttempt).IsEqualTo(1);
        using var doc = JsonDocument.Parse(output);
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("hi");
    }

    [Test]
    public async Task StepFile_PathFromAddressOrOperand_WritesTheOutputFile()
    {
        var api = Api();
        api.StepFileContent = "hello"u8.ToArray();
        var target = Path.Combine(Path.GetTempPath(), "pl-step-file-" + Path.GetRandomFileName());
        try
        {
            var (fromAddress, _) = await Run(new StepFileCommand(), api, "step", "file", $"{Name}#15/hello@1/dir/greeting.txt", "-o", target);
            await Assert.That(fromAddress.Succeeded).IsTrue();
            await Assert.That(api.LastPath).IsEqualTo("dir/greeting.txt");
            await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("hello");

            var (fromOperand, _) = await Run(new StepFileCommand(), api, "step", "file", $"{Name}#15/hello", "greeting.txt", "--output", target);
            await Assert.That(fromOperand.Succeeded).IsTrue();
            await Assert.That(api.LastPath).IsEqualTo("greeting.txt");

            var (both, _) = await Run(new StepFileCommand(), api, "step", "file", $"{Name}#15/hello/a.txt", "b.txt", "-o", target);
            await Assert.That(both.Succeeded).IsFalse();
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Test]
    public async Task StepFile_FailedDownload_LeavesNoFile()
    {
        var api = Api(); // StepFileContent null -> the fake reports a missing file
        var target = Path.Combine(Path.GetTempPath(), "pl-step-file-" + Path.GetRandomFileName());

        var (result, _) = await Run(new StepFileCommand(), api, "step", "file", $"{Name}#15/hello", "nope.txt", "-o", target);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(File.Exists(target)).IsFalse();
        await Assert.That(File.Exists(target + ".part")).IsFalse();
    }

    private static Attempt Attempt(int number, AttemptStatus status) => new() { RunNumber = 15, StepName = "hello", Number = number, Status = status };

    [Test]
    public async Task StepLogs_Follow_PrintsOnlyNewText_UntilTheAttemptFinishes()
    {
        var api = Api();
        api.ListStepAttemptsResults.Enqueue(Result.Success<Attempt[]>([Attempt(1, new InProgressStatus())]));
        api.ListStepAttemptsResults.Enqueue(Result.Success<Attempt[]>([Attempt(1, new InProgressStatus())]));
        api.ListStepAttemptsResults.Enqueue(Result.Success<Attempt[]>([Attempt(1, new SucceededStatus())]));
        api.GetStepLogsResults.Enqueue("line 1\n");
        api.GetStepLogsResults.Enqueue("line 1\nline 2\n");
        api.GetStepLogsResults.Enqueue("line 1\nline 2\nline 3\n");
        var delays = 0;
        var command = new StepLogsCommand { Delay = (_, _) => { delays++; return Task.CompletedTask; } };

        var (result, output) = await Run(command, api, "step", "logs", $"{Name}#15/hello", "-f");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(output).IsEqualTo("line 1\nline 2\nline 3\n");
        await Assert.That(delays).IsEqualTo(2);
        await Assert.That(api.LastAttempt).IsEqualTo(1);
    }

    [Test]
    public async Task StepLogs_Follow_StopsWhenTheRunEndedWithoutAnAttempt()
    {
        var api = Api();
        api.ListStepAttemptsResults.Enqueue(Result.Success<Attempt[]>([]));
        api.GetRunResult = new RunDetail { Run = new RunSummary { Number = 15, Status = "Cancelled" } };
        var command = new StepLogsCommand { Delay = (_, _) => Task.CompletedTask };

        var (result, _) = await Run(command, api, "step", "logs", $"{Name}#15/last", "--follow");

        await Assert.That(result.TryPickProblems(out var problems)).IsTrue();
        await Assert.That(problems!.First().ToString()).Contains("Cancelled");
    }

    [Test]
    public async Task StepLogs_Once_PrintsTheLatestAttemptsLog()
    {
        var api = Api();
        api.GetStepLogsResults.Enqueue("done");

        var (result, output) = await Run(new StepLogsCommand(), api, "step", "logs", $"{Name}#15/hello");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(output).IsEqualTo("done\n");
        await Assert.That(api.LastAttempt).IsNull();
        await Assert.That(api.Invoked("ListStepAttempts")).IsFalse();
    }

    [Test]
    public async Task GateBlockAndUnblock_ResolveTheNameAndStep()
    {
        var api = Api();
        api.BlockStepResult = Result.Success<string[]>(["last"]);
        api.UnblockStepResult = Result.Success<string[]>([]);

        var (blocked, blockedOut) = await Run(new StepBlockCommand(), api, "step", "block", $"{Name}/last");
        await Assert.That(blocked.Succeeded).IsTrue();
        await Assert.That(api.LastPipelineId).IsEqualTo(Id);
        await Assert.That(api.LastStep).IsEqualTo("last");
        await Assert.That(blockedOut).Contains("blocked: last");

        var (unblocked, unblockedOut) = await Run(new StepUnblockCommand(), api, "step", "unblock", $"{Name}/last");
        await Assert.That(unblocked.Succeeded).IsTrue();
        await Assert.That(unblockedOut).Contains("no blocked steps");
    }

    [Test]
    public async Task StepHistory_MarksDefinitionChanges()
    {
        var api = Api();
        api.GetStepHistoryResult = Result.Success<StepHistoryItem[]>(
        [
            new StepHistoryItem { RunNumber = 15, AttemptNumber = 1, Status = "succeeded", StepVersion = 2, StepVersionChanged = true, VersionNumber = 4 },
            new StepHistoryItem { RunNumber = 12, AttemptNumber = 1, Status = "skipped", Reason = "when mode", StepVersion = 1, VersionNumber = 3 },
        ]);

        var (result, output) = await Run(new StepHistoryCommand(), api, "step", "history", $"{Name}/last");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(output).Contains("* #15@1");
        await Assert.That(output).Contains("last.2");
        await Assert.That(output).Contains("when mode");
    }

    [Test]
    public async Task PipelineSwitch_PassesVersionAndCancelRunning()
    {
        var api = Api();
        api.SwitchPipelineVersionResult = new PipelineActivation { Type = "draining", FromVersionNumber = 4, ToVersionNumber = 3, CancelRunning = true };

        var (result, output) = await Run(new PipelineSwitchCommand(), api, "pipeline", "switch", Name, "3", "--cancel-running");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastRun).IsEqualTo(3); // the fake records the version here
        await Assert.That(api.LastFlag == true).IsTrue();
        await Assert.That(output).Contains("draining v4 -> v3");
    }

    [Test]
    public async Task PipelineManifest_DefaultsToTheActiveVersion_JsonPrintsTheRawManifest()
    {
        var api = Api();
        WithActiveVersion(api);

        var (human, humanOut) = await Run(new PipelineManifestCommand(), api, "pipeline", "manifest", Name);
        var (json, jsonOut) = await Run(new PipelineManifestCommand(), api, "pipeline", "manifest", Name, "--version", "4", "--json");

        await Assert.That(human.Succeeded).IsTrue();
        await Assert.That(humanOut).Contains("v4 · sha256:x");
        await Assert.That(humanOut).Contains("input:    In");
        await Assert.That(json.Succeeded).IsTrue();
        await Assert.That(jsonOut.Trim()).IsEqualTo(Manifest.Trim());
    }

    [Test]
    public async Task PipelineVersions_MarksTheCurrentVersion()
    {
        var api = Api();
        WithActiveVersion(api);

        var (result, output) = await Run(new PipelineVersionsCommand(), api, "pipeline", "versions", Name);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(output).Contains("activation: active v4");
        await Assert.That(output).Contains("> v4");
    }

    [Test]
    public async Task DevValidate_PostsTheDescribedManifest_InvalidExits78()
    {
        using var temp = new TempPipelineDir();
        var harness = new FakeHarness(DevFixtures.ManifestJson);
        var api = Api();
        api.ValidateManifestResult = new ManifestValidationResponse
        {
            Valid = false,
            Errors = [new ManifestIssueView { Code = "unknown-ref", Message = "no such step", Path = "stages[0]", Step = "build" }],
        };
        var command = new DevValidateCommand(harness, DevFixtures.Tools, new DevConsole(false, TextWriter.Null, TextWriter.Null));
        var (ctx, stdout, cli) = Build(command, api, "dev", "validate", "--dir", temp.Dir);

        var result = await command.Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastBody).IsEqualTo(DevFixtures.ManifestJson);
        await Assert.That(ctx.ExitCode).IsEqualTo(DevValidateCommand.InvalidExitCode);
        await Assert.That(stdout.ToString()).Contains("[unknown-ref] build: no such step");
    }

    [Test]
    public async Task Dispatcher_RoutesTheNewCommands()
    {
        // Unknown verbs on the new nouns are usage errors, and help for each noun lists its commands.
        var dispatcher = new CliDispatcher(new FakeHarness("{}"));
        foreach (var noun in new[] { "run", "step" })
            await Assert.That(await dispatcher.RunAsync([noun, "--help"])).IsEqualTo(0);
        await Assert.That(await dispatcher.RunAsync(["trigger", "--help"])).IsEqualTo(0);
        await Assert.That(await dispatcher.RunAsync(["run", "get"])).IsEqualTo(2); // missing address is a usage error
    }

    [Test]
    public async Task CriticalPath_PassesTheRange_AndListsTheRunsAStepWasOnThePathIn()
    {
        var api = Api();
        api.GetCriticalPathResult = Result.Success(new CriticalPathReport
        {
            From = 3,
            To = 4,
            TotalSeconds = 300,
            AverageSeconds = 150,
            Runs =
            [
                new CriticalPathRun { Number = 3, Status = "Succeeded", Seconds = 100, Path = [new CriticalPathStage { Stage = 1, Step = "test", Attempts = 2, Seconds = 100 }] },
                new CriticalPathRun { Number = 4, Status = "Succeeded", Seconds = 200, Path = [new CriticalPathStage { Stage = 1, Step = "build", Attempts = 1, Seconds = 200 }], Notes = ["build@2 left out"] },
            ],
            Steps =
            [
                new CriticalPathStep { Step = "build", Runs = 1, Seconds = 200, Share = 2.0 / 3, AverageSeconds = 200, RunNumbers = [4] },
                new CriticalPathStep { Step = "test", Runs = 1, Seconds = 100, Share = 1.0 / 3, AverageSeconds = 100, RunNumbers = [3] },
            ],
            Skipped = [new CriticalPathSkip { Number = 5, Reason = "Failed" }],
        });

        var (result, output) = await Run(new RunCriticalPathCommand(), api, "run", "critical-path", Name, "--from", "#3", "--to", "4", "--step", "test", "--all");

        await Assert.That(result).Succeeded();
        await Assert.That(api.LastPipelineId).IsEqualTo(Id);
        await Assert.That(api.LastCriticalPathQuery).IsEqualTo((3, 4, true, (int?)null));
        await Assert.That(output).Contains("test×2 1m40s");
        await Assert.That(output).Contains("66.7%");
        await Assert.That(output).Contains("#4: build@2 left out");
        await Assert.That(output).Contains("#5: not reported (Failed)");
        await Assert.That(output).Contains("test was on the critical path in 1/2 run(s): #3 (1m40s)");
    }

    [Test]
    public async Task CriticalPath_RejectsANonNumberRange()
    {
        var (result, _) = await Run(new RunCriticalPathCommand(), Api(), "run", "critical-path", Name, "--from", "abc");
        await Assert.That(result).Failed();
    }
}
