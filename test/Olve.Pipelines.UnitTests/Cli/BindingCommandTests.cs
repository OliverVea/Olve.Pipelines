using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Bindings;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.UnitTests.Cli;

public class BindingCommandTests
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

    private static PipelineConfigBinding SampleBinding() => new()
    {
        Id = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        Repo = "OliverVea/Olve.Pipelines",
        Branch = "main",
        Path = ".pipelines",
        CredentialsSecret = "GITHUB_TOKEN",
        Status = new ReconcileStatus { Result = ReconcileResult.Success, Sha = new string('d', 40), VersionNumber = 4, Warnings = ["[W] w1"] },
    };

    [Test]
    public async Task BindingCreate_PassesOptions_AndPrintsBinding()
    {
        var api = new FakePipelinesApi { CreatePipelineWithRepoResult = Result.Success(SampleBinding()) };
        var (ctx, stdout, cli) = Build(new BindingCreateCommand(), api, json: false,
            "binding", "create", "OliverVea/Olve.Pipelines",
            "--branch", "develop", "--path", "deploy", "--credentials-secret", "GH");

        var result = await new BindingCreateCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("CreatePipelineWithRepo")).IsTrue();
        await Assert.That(api.LastCreateWithRepo).IsEqualTo(
            ("OliverVea/Olve.Pipelines", "develop", "deploy", "GH"));
        await Assert.That(stdout.ToString()).Contains("OliverVea/Olve.Pipelines");
    }

    [Test]
    public async Task BindingCreate_NoOptions_SendsNulls()
    {
        var api = new FakePipelinesApi { CreatePipelineWithRepoResult = Result.Success(SampleBinding()) };
        var (ctx, _, cli) = Build(new BindingCreateCommand(), api, json: false,
            "binding", "create", "OliverVea/Olve.Pipelines");

        var result = await new BindingCreateCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastCreateWithRepo).IsEqualTo(
            ("OliverVea/Olve.Pipelines", (string?)null, (string?)null, (string?)null));
    }

    [Test]
    public async Task BindingGet_PrintsRepoAndStatus()
    {
        var api = new FakePipelinesApi { GetPipelineBindingResult = Result.Success(SampleBinding()) };
        var (ctx, stdout, cli) = Build(new BindingGetCommand(), api, json: false, "binding", "get", "11111111-2222-3333-4444-555555555555");

        var result = await new BindingGetCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("GetPipelineBinding")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("OliverVea/Olve.Pipelines");
        await Assert.That(stdout.ToString()).Contains("v4");
        await Assert.That(stdout.ToString()).Contains("[W] w1");
    }

    [Test]
    public async Task BindingStatus_PrintsReconcileVersionAndWarnings()
    {
        var status = new PipelineBindingStatus
        {
            PipelineId = Guid.NewGuid(),
            Repo = "OliverVea/Olve.Pipelines",
            Branch = "main",
            Path = ".pipelines",
            Result = ReconcileResult.Success,
            ReconcileSha = new string('c', 40),
            ReconcileInProgress = true,
            ReconciledVersion = 7,
            Activation = new PipelineActivation { Type = "active", VersionNumber = 7 },
            Warnings = ["[W] stages: unused output"],
        };
        var api = new FakePipelinesApi { GetPipelineBindingStatusResult = Result.Success(status) };
        var (ctx, stdout, cli) = Build(new BindingStatusCommand(), api, json: false, "binding", "status", "11111111-2222-3333-4444-555555555555");

        var result = await new BindingStatusCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("GetPipelineBindingStatus")).IsTrue();
        var output = stdout.ToString();
        await Assert.That(output).Contains("in progress");
        await Assert.That(output).Contains("v7");
        await Assert.That(output).Contains("active v7");
        await Assert.That(output).Contains("unused output");
    }

    [Test]
    public async Task BindingSetCredentials_WithName_SendsName()
    {
        var api = new FakePipelinesApi { UpdatePipelineBindingCredentialsResult = Result.Success(SampleBinding()) };
        var (ctx, _, cli) = Build(new BindingSetCredentialsCommand(), api, json: false,
            "binding", "set-credentials", "11111111-2222-3333-4444-555555555555", "GITHUB_TOKEN");

        var result = await new BindingSetCredentialsCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastCredentialsSecret).IsEqualTo("GITHUB_TOKEN");
    }

    [Test]
    public async Task BindingSetCredentials_NoName_ClearsWithNull()
    {
        var api = new FakePipelinesApi { UpdatePipelineBindingCredentialsResult = Result.Success(SampleBinding()) };
        var (ctx, _, cli) = Build(new BindingSetCredentialsCommand(), api, json: false,
            "binding", "set-credentials", "11111111-2222-3333-4444-555555555555");

        var result = await new BindingSetCredentialsCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("UpdatePipelineBindingCredentials((null))")).IsTrue();
        await Assert.That(api.LastCredentialsSecret).IsNull();
    }

    [Test]
    public async Task BindingSetBranch_SendsBranch()
    {
        var api = new FakePipelinesApi { SetPipelineBindingBranchResult = Result.Success(SampleBinding()) };
        var (ctx, _, cli) = Build(new BindingSetBranchCommand(), api, json: false,
            "binding", "set-branch", "11111111-2222-3333-4444-555555555555", "main");

        var result = await new BindingSetBranchCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("SetPipelineBindingBranch(11111111-2222-3333-4444-555555555555,main)")).IsTrue();
    }

    [Test]
    public async Task BindingReconcile_CallsApi_AndConfirms()
    {
        var api = new FakePipelinesApi { ReconcilePipelineBindingResult = Result.Success() };
        var (ctx, stdout, cli) = Build(new BindingReconcileCommand(), api, json: false,
            "binding", "reconcile", "11111111-2222-3333-4444-555555555555");

        var result = await new BindingReconcileCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ReconcilePipelineBinding")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("11111111-2222-3333-4444-555555555555");
    }
}
