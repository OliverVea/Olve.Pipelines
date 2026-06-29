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
        DeployTrigger = BindingDeployTrigger.Webhook,
        Status = new ReconcileStatus { Result = ReconcileResult.Success },
    };

    [Test]
    public async Task BindingCreate_PassesOptions_AndPrintsBinding()
    {
        var api = new FakePipelinesApi { CreatePipelineWithRepoResult = Result.Success(SampleBinding()) };
        var (ctx, stdout, cli) = Build(new BindingCreateCommand(), api, json: false,
            "binding", "create", "OliverVea/Olve.Pipelines",
            "--branch", "develop", "--path", "deploy", "--credentials-secret", "GH", "--trigger", "poll");

        var result = await new BindingCreateCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("CreatePipelineWithRepo")).IsTrue();
        await Assert.That(api.LastCreateWithRepo).IsEqualTo(
            ("OliverVea/Olve.Pipelines", "develop", "deploy", "GH", (BindingDeployTrigger?)BindingDeployTrigger.Poll));
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
            ("OliverVea/Olve.Pipelines", (string?)null, (string?)null, (string?)null, (BindingDeployTrigger?)null));
    }

    [Test]
    public async Task BindingCreate_BadTrigger_Fails_WithoutCallingApi()
    {
        var api = new FakePipelinesApi(); // result intentionally unset — must not be called
        var (ctx, _, cli) = Build(new BindingCreateCommand(), api, json: false,
            "binding", "create", "OliverVea/Olve.Pipelines", "--trigger", "nonsense");

        var result = await new BindingCreateCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(api.Invoked("CreatePipelineWithRepo")).IsFalse();
    }

    [Test]
    public async Task BindingGet_PrintsRepoAndTrigger()
    {
        var api = new FakePipelinesApi { GetPipelineBindingResult = Result.Success(SampleBinding()) };
        var (ctx, stdout, cli) = Build(new BindingGetCommand(), api, json: false, "binding", "get", "pid");

        var result = await new BindingGetCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("GetPipelineBinding")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("OliverVea/Olve.Pipelines");
        await Assert.That(stdout.ToString()).Contains("webhook");
    }

    [Test]
    public async Task BindingStatus_PrintsReconcileAndSecrets()
    {
        var status = new PipelineBindingStatus
        {
            PipelineId = Guid.NewGuid(),
            Repo = "OliverVea/Olve.Pipelines",
            Branch = "main",
            Path = ".pipelines",
            Result = ReconcileResult.Success,
            Secrets =
            [
                new SecretStatus { Name = "GITHUB_TOKEN", IsSet = true },
                new SecretStatus { Name = "SSH_PRIVATE_KEY", IsSet = false },
            ],
        };
        var api = new FakePipelinesApi { GetPipelineBindingStatusResult = Result.Success(status) };
        var (ctx, stdout, cli) = Build(new BindingStatusCommand(), api, json: false, "binding", "status", "pid");

        var result = await new BindingStatusCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("GetPipelineBindingStatus")).IsTrue();
        var output = stdout.ToString();
        await Assert.That(output).Contains("GITHUB_TOKEN");
        await Assert.That(output).Contains("set");
        await Assert.That(output).Contains("unset");
    }

    [Test]
    public async Task BindingSetCredentials_WithName_SendsName()
    {
        var api = new FakePipelinesApi { UpdatePipelineBindingCredentialsResult = Result.Success(SampleBinding()) };
        var (ctx, _, cli) = Build(new BindingSetCredentialsCommand(), api, json: false,
            "binding", "set-credentials", "pid", "GITHUB_TOKEN");

        var result = await new BindingSetCredentialsCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastCredentialsSecret).IsEqualTo("GITHUB_TOKEN");
    }

    [Test]
    public async Task BindingSetCredentials_NoName_ClearsWithNull()
    {
        var api = new FakePipelinesApi { UpdatePipelineBindingCredentialsResult = Result.Success(SampleBinding()) };
        var (ctx, _, cli) = Build(new BindingSetCredentialsCommand(), api, json: false,
            "binding", "set-credentials", "pid");

        var result = await new BindingSetCredentialsCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("UpdatePipelineBindingCredentials((null))")).IsTrue();
        await Assert.That(api.LastCredentialsSecret).IsNull();
    }

    [Test]
    public async Task BindingSetTrigger_ParsesWebhookOnly()
    {
        var api = new FakePipelinesApi { UpdatePipelineBindingDeployTriggerResult = Result.Success(SampleBinding()) };
        var (ctx, _, cli) = Build(new BindingSetTriggerCommand(), api, json: false,
            "binding", "set-trigger", "pid", "webhook-only");

        var result = await new BindingSetTriggerCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastDeployTrigger).IsEqualTo(BindingDeployTrigger.WebhookOnly);
    }

    [Test]
    public async Task BindingSetTrigger_BadValue_Fails_WithoutCallingApi()
    {
        var api = new FakePipelinesApi();
        var (ctx, _, cli) = Build(new BindingSetTriggerCommand(), api, json: false,
            "binding", "set-trigger", "pid", "nonsense");

        var result = await new BindingSetTriggerCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(api.Invoked("UpdatePipelineBindingDeployTrigger(WebhookOnly)")).IsFalse();
    }

    [Test]
    public async Task BindingReconcile_CallsApi_AndConfirms()
    {
        var api = new FakePipelinesApi { ReconcilePipelineBindingResult = Result.Success() };
        var (ctx, stdout, cli) = Build(new BindingReconcileCommand(), api, json: false,
            "binding", "reconcile", "pid");

        var result = await new BindingReconcileCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ReconcilePipelineBinding")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("pid");
    }
}
