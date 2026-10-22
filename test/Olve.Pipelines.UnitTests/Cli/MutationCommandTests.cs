using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Attempts;
using Olve.Pipelines.Cli.Commands.Pipelines;
using Olve.Pipelines.Cli.Commands.Steps;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.UnitTests.Cli;

public class MutationCommandTests
{
    private static (CommandContext Ctx, StringWriter Out, CliArgs Cli) Build(
        ICliCommand command, FakePipelinesApi api, bool json, params string[] args)
    {
        var booleans = new HashSet<string>(command.BooleanFlags, StringComparer.Ordinal) { "json", "verbose" };
        CliArgs.Parse(args, booleans, command.Aliases).TryPickProblems(out _, out var cli);
        cli!.OperandOffset = 2; // "<noun>" "<verb>"

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

    private const string PipelineGuid = "11111111-2222-3333-4444-555555555555";

    [Test]
    public async Task StepCancel_CallsApiWithTheAddress_AndPrintsTheAttempt()
    {
        var api = new FakePipelinesApi
        {
            CancelStepResult = new Attempt { RunNumber = 12, StepName = "deploy", Number = 2, Status = new CancelledStatus() },
        };
        var (ctx, stdout, cli) = Build(new StepCancelCommand(), api, json: false, "step", "cancel", $"{PipelineGuid}#12/deploy");

        var result = await new StepCancelCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastPipelineId).IsEqualTo(PipelineGuid);
        await Assert.That(api.LastRun).IsEqualTo(12);
        await Assert.That(api.LastStep).IsEqualTo("deploy");
        await Assert.That(stdout.ToString()).Contains("cancelled");
    }

    [Test]
    public async Task StepCancel_RejectsAnAttemptNumber()
    {
        var api = new FakePipelinesApi();
        var (ctx, _, cli) = Build(new StepCancelCommand(), api, json: false, "step", "cancel", $"{PipelineGuid}#12/deploy@2");

        var result = await new StepCancelCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(api.Invoked("CancelStep")).IsFalse();
    }

    [Test]
    public async Task Attempts_ListsWhatIsRunning_ByAddress()
    {
        var api = new FakePipelinesApi
        {
            ListActiveAttemptsResult = new ActiveAttempt[]
            {
                new() { PipelineName = "web", Run = 7, Step = "build", Attempt = 1, Status = "in-progress", CreatedAt = DateTimeOffset.UtcNow },
            },
        };
        var command = new AttemptListCommand();
        var (ctx, stdout, cli) = Build(command, api, json: false, "attempts");
        cli.OperandOffset = 1;

        var result = await command.Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(stdout.ToString()).Contains("web#7/build@1");
        await Assert.That(stdout.ToString()).Contains("in-progress");
    }

    [Test]
    public async Task PipelineDelete_CallsApi_AndConfirms()
    {
        var api = new FakePipelinesApi { DeletePipelineResult = Result.Success() };
        var (ctx, stdout, cli) = Build(new PipelineDeleteCommand(), api, json: false, "pipeline", "delete", "11111111-2222-3333-4444-555555555555");

        var result = await new PipelineDeleteCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("DeletePipeline")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("11111111-2222-3333-4444-555555555555");
    }

    [Test]
    public async Task Mutation_ApiFailure_IsPropagated()
    {
        var api = new FakePipelinesApi
        {
            CancelStepResult = new ResultProblem("Step 'deploy' has no scheduled or in-progress attempt in run #12 (latest is succeeded)."),
        };
        var (ctx, _, cli) = Build(new StepCancelCommand(), api, json: false, "step", "cancel", $"{PipelineGuid}#12/deploy");

        var result = await new StepCancelCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
    }
}
