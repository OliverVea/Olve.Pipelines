using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Secrets;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.UnitTests.Cli;

public class SecretCommandTests
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
    public async Task SecretList_PrintsNames()
    {
        var api = new FakePipelinesApi { ListSecretsResult = Result.Success<string[]>(["REGISTRY_TOKEN", "DEPLOY_KEY"]) };
        var (ctx, stdout, cli) = Build(new SecretListCommand(), api, json: false, "secret", "list", "pid");

        var result = await new SecretListCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ListSecrets")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("REGISTRY_TOKEN");
        await Assert.That(stdout.ToString()).Contains("DEPLOY_KEY");
    }

    [Test]
    public async Task SecretSet_FromEnv_SendsEnvValue()
    {
        var envVar = "PL_TEST_SECRET_FROMENV";
        Environment.SetEnvironmentVariable(envVar, "s3cr3t-value");
        try
        {
            var api = new FakePipelinesApi { SetSecretResult = Result.Success() };
            var (ctx, stdout, cli) = Build(new SecretSetCommand(), api, json: false,
                "secret", "set", "pid", "REGISTRY_TOKEN", "--from-env", envVar);

            var result = await new SecretSetCommand().Execute(cli, ctx, CancellationToken.None);

            await Assert.That(result.Succeeded).IsTrue();
            await Assert.That(api.Invoked("SetSecret(REGISTRY_TOKEN)")).IsTrue();
            await Assert.That(api.LastSecretValue).IsEqualTo("s3cr3t-value");
            await Assert.That(stdout.ToString()).Contains("REGISTRY_TOKEN");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Test]
    public async Task SecretSet_FromEnv_MissingVar_Fails_WithoutCallingApi()
    {
        var api = new FakePipelinesApi(); // SetSecretResult intentionally unset — must not be called
        var (ctx, _, cli) = Build(new SecretSetCommand(), api, json: false,
            "secret", "set", "pid", "TOKEN", "--from-env", "PL_TEST_SECRET_DEFINITELY_UNSET");

        var result = await new SecretSetCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(api.Invoked("SetSecret(TOKEN)")).IsFalse();
    }

    [Test]
    public async Task SecretSet_FromFile_SendsFileContentVerbatim()
    {
        var path = Path.GetTempFileName();
        // A multi-line value with a trailing newline — must round-trip verbatim (e.g. PEM keys).
        var contents = "line1\nline2\n";
        await File.WriteAllTextAsync(path, contents);
        try
        {
            var api = new FakePipelinesApi { SetSecretResult = Result.Success() };
            var (ctx, _, cli) = Build(new SecretSetCommand(), api, json: false,
                "secret", "set", "pid", "DEPLOY_KEY", "--from-file", path);

            var result = await new SecretSetCommand().Execute(cli, ctx, CancellationToken.None);

            await Assert.That(result.Succeeded).IsTrue();
            await Assert.That(api.LastSecretValue).IsEqualTo(contents);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SecretSet_FromEnvAndFile_IsRejected()
    {
        var api = new FakePipelinesApi();
        var (ctx, _, cli) = Build(new SecretSetCommand(), api, json: false,
            "secret", "set", "pid", "TOKEN", "--from-env", "X", "--from-file", "y");

        var result = await new SecretSetCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(api.Invoked("SetSecret(TOKEN)")).IsFalse();
    }

    [Test]
    public async Task SecretDelete_CallsApi_AndConfirms()
    {
        var api = new FakePipelinesApi { DeleteSecretResult = Result.Success() };
        var (ctx, stdout, cli) = Build(new SecretDeleteCommand(), api, json: false,
            "secret", "delete", "pid", "REGISTRY_TOKEN");

        var result = await new SecretDeleteCommand().Execute(cli, ctx, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("DeleteSecret(REGISTRY_TOKEN)")).IsTrue();
        await Assert.That(stdout.ToString()).Contains("REGISTRY_TOKEN");
    }
}
