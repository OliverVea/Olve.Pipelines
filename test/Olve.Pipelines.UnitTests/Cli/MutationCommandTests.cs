using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Jobs;
using Olve.Pipelines.Cli.Commands.Processing;
using Olve.Pipelines.Cli.Commands.Production;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.UnitTests.Cli;

public class MutationCommandTests
{
    private static readonly Guid GroupId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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

    [Test]
    public async Task ProductionTrigger_CallsApi_AndPrintsGroupId()
    {
        var api = new FakePipelinesApi
        {
            TriggerProductionResult = Result.Success<JobGroup>(new ProductionJobGroup { Id = GroupId }),
        };
        var (ctx, stdout, cli) = Build(new ProductionTriggerCommand(), api, json: false, "production", "trigger", "pid");

        var result = await new ProductionTriggerCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("TriggerProduction")).IsTrue();
        await Assert.That(stdout.ToString()).Contains(GroupId.ToString());
    }

    [Test]
    public async Task ProcessingBlock_SetsGateBlockedTrue()
    {
        var api = new FakePipelinesApi
        {
            SetProcessingStepPromotionResult = Result.Success<PromotionState>(new PromotionState { Blocked = true }),
        };
        var (ctx, stdout, cli) = Build(new ProcessingBlockCommand(), api, json: false, "processing", "block", "sid");

        var result = await new ProcessingBlockCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("SetProcessingStepPromotion(True)")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("blocked");
    }

    [Test]
    public async Task ProcessingUnblock_SetsGateBlockedFalse()
    {
        var api = new FakePipelinesApi
        {
            SetProcessingStepPromotionResult = Result.Success<PromotionState>(new PromotionState { Blocked = false }),
        };
        var (ctx, stdout, cli) = Build(new ProcessingUnblockCommand(), api, json: false, "processing", "unblock", "sid");

        var result = await new ProcessingUnblockCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("SetProcessingStepPromotion(False)")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("enabled");
    }

    [Test]
    public async Task ProcessingRePromote_CallsApi()
    {
        var api = new FakePipelinesApi
        {
            RePromoteProcessingStepResult = Result.Success<JobGroup>(new ProcessingJobGroup { Id = GroupId }),
        };
        var (ctx, _, cli) = Build(new ProcessingRePromoteCommand(), api, json: false, "processing", "re-promote", "sid");

        var result = await new ProcessingRePromoteCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("RePromoteProcessingStep")).IsTrue();
    }

    [Test]
    public async Task JobCancel_CallsApi_AndConfirms()
    {
        var api = new FakePipelinesApi { CancelJobResult = Result.Success() };
        var (ctx, stdout, cli) = Build(new JobCancelCommand(), api, json: false, "job", "cancel", "jid");

        var result = await new JobCancelCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("CancelJob")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("jid");
    }

    [Test]
    public async Task Mutation_ApiFailure_IsPropagated()
    {
        var api = new FakePipelinesApi
        {
            TriggerProductionResult = (Result<JobGroup>)new ResultProblem("Pipeline 'pid' has no configured production steps."),
        };
        var (ctx, _, cli) = Build(new ProductionTriggerCommand(), api, json: false, "production", "trigger", "pid");

        var result = await new ProductionTriggerCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
    }
}
