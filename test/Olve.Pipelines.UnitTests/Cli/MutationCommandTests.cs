using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Jobs;
using Olve.Pipelines.Cli.Commands.Pipelines;
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
    public async Task PipelineDelete_CallsApi_AndConfirms()
    {
        var api = new FakePipelinesApi { DeletePipelineResult = Result.Success() };
        var (ctx, stdout, cli) = Build(new PipelineDeleteCommand(), api, json: false, "pipeline", "delete", "pid");

        var result = await new PipelineDeleteCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("DeletePipeline")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("pid");
    }

    [Test]
    public async Task Mutation_ApiFailure_IsPropagated()
    {
        var api = new FakePipelinesApi
        {
            CancelJobResult = new ResultProblem("Job 'jid' cannot be cancelled because it is Done."),
        };
        var (ctx, _, cli) = Build(new JobCancelCommand(), api, json: false, "job", "cancel", "jid");

        var result = await new JobCancelCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
    }
}
