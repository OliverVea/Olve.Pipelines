using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Jobs;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.UnitTests.Cli;

public class JobListCommandTests
{
    private static readonly Guid PipelineId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StepId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid JobId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static (CommandContext Ctx, StringWriter Out, CliArgs Cli) Build(
        FakePipelinesApi api, bool json, params string[] args)
    {
        ICliCommand command = new JobListCommand();
        var booleans = new HashSet<string>(command.BooleanFlags, StringComparer.Ordinal) { "json", "verbose" };
        CliArgs.Parse(args, booleans, command.Aliases).TryPickProblems(out _, out var cli);
        cli!.OperandOffset = 2; // "job" "list"

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

    private static FakePipelinesApi ApiWithOneProductionJob() => new()
    {
        ListJobsResult = Result.Success<PageOfJob>(new PageOfJob
        {
            Items = [new ProductionJob { Id = JobId, PipelineId = PipelineId, ProductionStepId = StepId }],
            PageNumber = 0,
            PageSize = 50,
            TotalCount = 1,
        }),
        ListProductionStepsResult = Result.Success<ProductionStep[]>(
            [new ProductionStep { Id = StepId, Name = "build-backend", PipelineId = PipelineId }]),
        ListProcessingStepsResult = Result.Success<ProcessingStep[]>([]),
    };

    [Test]
    public async Task List_Human_ResolvesAndRendersStepName()
    {
        var api = ApiWithOneProductionJob();
        var (ctx, stdout, cli) = Build(api, json: false, "job", "list");

        var result = await new JobListCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ListProductionSteps")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("STEP");
        await Assert.That(stdout.ToString()).Contains("build-backend");
    }

    [Test]
    public async Task List_Json_SkipsStepNameResolution()
    {
        var api = ApiWithOneProductionJob();
        var (ctx, stdout, cli) = Build(api, json: true, "job", "list", "--json");

        var result = await new JobListCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ListProductionSteps")).IsFalse();
        await Assert.That(stdout.ToString().Trim().StartsWith('{')).IsTrue();
    }
}
