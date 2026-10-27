using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Config;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;
using Olve.Results;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Cli;

public class ConfigCommandTests
{
    private const string PipelineId = "11111111-2222-3333-4444-555555555555";

    private static (CommandContext Ctx, StringWriter Out, CliArgs Cli) Build(ICliCommand command, FakePipelinesApi api, params string[] args)
    {
        var booleans = new HashSet<string>(command.BooleanFlags, StringComparer.Ordinal) { "json", "verbose" };
        CliArgs.Parse(args, booleans, command.Aliases).TryPickProblems(out _, out var cli);
        cli!.OperandOffset = 2;
        var stdout = new StringWriter();
        var ctx = new CommandContext
        {
            Json = false,
            Verbose = false,
            Api = api,
            Output = new ConsoleOutputWriter(false, stdout),
            Log = new StderrLog(false, TextWriter.Null),
            Config = new CliConfig(),
        };
        return (ctx, stdout, cli);
    }

    [Test]
    public async Task Get_PrintsTheYamlAsSaved()
    {
        var api = new FakePipelinesApi
        {
            GetPipelineConfigResult = new ConfigDocument
            {
                Snapshot = new ConfigSnapshotInfo { Number = 3, Author = "dev" },
                Yaml = "# comment\ntarget: {host: h}\n",
            },
        };
        var (ctx, stdout, cli) = Build(new ConfigGetCommand(), api, "config", "get", PipelineId, "--snapshot", "3");

        var result = await new ConfigGetCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result).Succeeded();
        await Assert.That(api.Invoked("GetPipelineConfig(3)")).IsTrue();
        await Assert.That(stdout.ToString().ReplaceLineEndings("\n")).IsEqualTo("# comment\ntarget: {host: h}\n");
    }

    [Test]
    public async Task Set_SendsTheFile_AndReportsTheSnapshotAndPendingVersion()
    {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "auth: {mode: dex}\n");
        var api = new FakePipelinesApi
        {
            SetPipelineConfigResult = new ConfigSaveResponse
            {
                Snapshot = new ConfigSnapshotInfo { Number = 5 },
                Pending = new ConfigCheckResponse { Version = 9, Problems = ["config.auth.clientId: is required"], MissingSecrets = ["DEX_CLIENT_SECRET"] },
            },
        };
        var (ctx, stdout, cli) = Build(new ConfigSetCommand(), api, "config", "set", PipelineId, "-f", file);

        var result = await new ConfigSetCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result).Succeeded();
        await Assert.That(api.LastConfigYaml).IsEqualTo("auth: {mode: dex}\n");
        await Assert.That(stdout.ToString()).Contains("Saved config #5");
        await Assert.That(stdout.ToString()).Contains("pending v9: config config.auth.clientId: is required; secret DEX_CLIENT_SECRET not set");
    }

    [Test]
    public async Task Rollback_SendsTheSnapshotNumber()
    {
        var api = new FakePipelinesApi
        {
            RollbackPipelineConfigResult = new ConfigSaveResponse { Snapshot = new ConfigSnapshotInfo { Number = 7, RestoredFrom = 2 } },
        };
        var (ctx, stdout, cli) = Build(new ConfigRollbackCommand(), api, "config", "rollback", PipelineId, "2");

        var result = await new ConfigRollbackCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result).Succeeded();
        await Assert.That(api.Invoked("RollbackPipelineConfig(2)")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("Saved config #7").And.Contains("content of #2");
    }

    [Test]
    public async Task Check_ListsReasonsAndChangedDefaults_AndFailsWhenUnsatisfied()
    {
        var api = new FakePipelinesApi
        {
            CheckPipelineConfigResult = new ConfigCheckResponse
            {
                Version = 9,
                Valid = true,
                MissingSecrets = ["DEX_CLIENT_SECRET"],
                ChangedDefaults = [new ChangedDefault { Path = "telemetry.exporter", From = "\"none\"", To = "\"otlp\"" }],
            },
        };
        var (ctx, stdout, cli) = Build(new ConfigCheckCommand(), api, "config", "check", PipelineId, "--version", "9");

        var result = await new ConfigCheckCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result).Failed();
        await Assert.That(api.Invoked("CheckPipelineConfig(9)")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("secret DEX_CLIENT_SECRET not set");
        await Assert.That(stdout.ToString()).Contains("v9 changes the default of telemetry.exporter (\"none\" → \"otlp\")");
    }

    [Test]
    public async Task PendingVersion_DescribesItselfOnOneLine()
    {
        var pending = new PendingVersionInfo { Version = 9, Reasons = ["config config.auth.clientId: is required", "secret X not set"] };

        await Assert.That(pending.Describe()).IsEqualTo("pending v9: config config.auth.clientId: is required; secret X not set");
    }
}
