using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Pipelines;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.UnitTests.Cli;

public class PipelineListCommandTests
{
    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static (CommandContext Ctx, StringWriter Out, CliArgs Cli) Build(
        FakePipelinesApi api, bool json, params string[] args)
    {
        ICliCommand command = new PipelineListCommand();
        var booleans = new HashSet<string>(command.BooleanFlags, StringComparer.Ordinal) { "json", "verbose" };
        CliArgs.Parse(args, booleans, command.Aliases).TryPickProblems(out _, out var cli);
        cli!.OperandOffset = 2; // "pipeline" "list"

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
    public async Task List_Human_RendersIdAndName()
    {
        var api = new FakePipelinesApi
        {
            ListPipelinesResult = Result.Success<Pipeline[]>([new Pipeline { Id = Id1, Name = "alpha" }]),
        };
        var (ctx, stdout, cli) = Build(api, json: false, "pipeline", "list");

        var result = await new PipelineListCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ListPipelines")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("alpha");
        await Assert.That(stdout.ToString()).Contains(Id1.ToString());
    }

    [Test]
    public async Task List_Json_EmitsJsonArray()
    {
        var api = new FakePipelinesApi
        {
            ListPipelinesResult = Result.Success<Pipeline[]>([new Pipeline { Id = Id1, Name = "alpha" }]),
        };
        var (ctx, stdout, cli) = Build(api, json: true, "pipeline", "list", "--json");

        var result = await new PipelineListCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        var output = stdout.ToString().Trim();
        await Assert.That(output.StartsWith('[')).IsTrue();
        await Assert.That(output).Contains("\"name\"");
    }

    [Test]
    public async Task Summaries_CallsListPipelineSummaries()
    {
        var api = new FakePipelinesApi
        {
            ListPipelineSummariesResult = Result.Success<PipelineSummary[]>(
                [new PipelineSummary { Id = Id1, Name = "alpha", Repo = "r" }]),
        };
        var (ctx, _, cli) = Build(api, json: false, "pipeline", "list", "--summaries");

        var result = await new PipelineListCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ListPipelineSummaries")).IsTrue();
    }

    [Test]
    public async Task Failure_FromApi_IsPropagated()
    {
        var api = new FakePipelinesApi
        {
            ListPipelinesResult = (Result<Pipeline[]>)new ResultProblem("Not authenticated (401). Run `pl login`."),
        };
        var (ctx, _, cli) = Build(api, json: false, "pipeline", "list");

        var result = await new PipelineListCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        result.TryPickProblems(out var problems);
        await Assert.That(problems?.Any(p => p.ToString()!.Contains("pl login")) ?? false).IsTrue();
    }
}
