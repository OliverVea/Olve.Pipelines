using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Secrets;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Cli;

public class SecretCopyCommandTests
{
    private static readonly Guid SourceId = Guid.Parse("f26d19cf-810a-4fb8-b5af-e61ca73b1bae");
    private static readonly Guid TargetId = Guid.Parse("ad01bc3f-992f-4695-8bce-6389b63ce23f");

    private static (CommandContext Ctx, StringWriter Out, CliArgs Cli) Build(FakePipelinesApi api, bool json, params string[] args)
    {
        ICliCommand command = new SecretCopyCommand();
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

    private static FakePipelinesApi Api(params string[] copied) => new()
    {
        CopySecretsResult = Result.Success(new CopySecretsResponse { Copied = copied }),
        ListPipelinesResult = Result.Success<Pipeline[]>(
        [
            new Pipeline { Id = SourceId, Name = "Olve.Pipelines v2" },
            new Pipeline { Id = TargetId, Name = "fixture" },
        ]),
    };

    private static Task<Result> Run(FakePipelinesApi api, CommandContext ctx, CliArgs cli) =>
        new SecretCopyCommand().Execute(cli, ctx, CancellationToken.None);

    [Test]
    public async Task CopyOne_ByIds_SendsSourceAndName_ToTarget()
    {
        var api = Api("GITHUB_TOKEN");
        var (ctx, stdout, cli) = Build(api, json: false, "secret", "copy", $"{SourceId}/GITHUB_TOKEN", TargetId.ToString());

        var result = await Run(api, ctx, cli);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Invoked("ListPipelines")).IsFalse();
        var (target, request) = api.LastCopySecrets!.Value;
        await Assert.That(target).IsEqualTo(TargetId.ToString());
        await Assert.That(request.SourcePipelineId).IsEqualTo(SourceId);
        await Assert.That(request.Names).IsEquivalentTo(["GITHUB_TOKEN"]);
        await Assert.That(request.TargetName).IsNull();
        await Assert.That(request.Overwrite).IsFalse();
        await Assert.That(stdout.ToString()).Contains("Copied GITHUB_TOKEN");
    }

    [Test]
    public async Task CopyOne_ByNames_ResolvesBothPipelines()
    {
        var api = Api("GITHUB_TOKEN");
        var (ctx, _, cli) = Build(api, json: false, "secret", "copy", "Olve.Pipelines v2/GITHUB_TOKEN", "fixture");

        var result = await Run(api, ctx, cli);

        await Assert.That(result.Succeeded).IsTrue();
        var (target, request) = api.LastCopySecrets!.Value;
        await Assert.That(target).IsEqualTo(TargetId.ToString());
        await Assert.That(request.SourcePipelineId).IsEqualTo(SourceId);
    }

    [Test]
    public async Task NoName_CopiesAll()
    {
        var api = Api("A", "B");
        var (ctx, _, cli) = Build(api, json: false, "secret", "copy", "Olve.Pipelines v2", "fixture");

        var result = await Run(api, ctx, cli);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.LastCopySecrets!.Value.Request.Names).IsNull();
    }

    [Test]
    public async Task RenameAndOverwrite_ArePassedThrough()
    {
        var api = Api("GH");
        var (ctx, _, cli) = Build(api, json: false,
            "secret", "copy", "fixture/GITHUB_TOKEN", "fixture", "--name", "GH", "--overwrite");

        var result = await Run(api, ctx, cli);

        await Assert.That(result.Succeeded).IsTrue();
        var request = api.LastCopySecrets!.Value.Request;
        await Assert.That(request.TargetName).IsEqualTo("GH");
        await Assert.That(request.Overwrite).IsTrue();
    }

    [Test]
    public async Task Rename_WithCopyAll_Fails_WithoutCallingApi()
    {
        var api = new FakePipelinesApi(); // unconfigured — any call throws
        var (ctx, _, cli) = Build(api, json: false, "secret", "copy", SourceId.ToString(), TargetId.ToString(), "--name", "GH");

        var result = await Run(api, ctx, cli);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(api.Calls).IsEmpty();
    }

    [Test]
    public async Task UnknownPipelineName_Fails_WithoutCopying()
    {
        var api = Api();
        var (ctx, _, cli) = Build(api, json: false, "secret", "copy", "nope/X", "fixture");

        var result = await Run(api, ctx, cli);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(api.Invoked("CopySecrets")).IsFalse();
    }

    [Test]
    public async Task Json_EmitsCopiedNames()
    {
        var api = Api("GITHUB_TOKEN");
        var (ctx, stdout, cli) = Build(api, json: true, "secret", "copy", $"{SourceId}/GITHUB_TOKEN", TargetId.ToString());

        var result = await Run(api, ctx, cli);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(stdout.ToString()).Contains("\"copied\"");
        await Assert.That(stdout.ToString()).Contains("GITHUB_TOKEN");
    }

    [Test]
    [Arguments("p/NAME", "p", "NAME")]
    [Arguments("p", "p", null)]
    [Arguments("p/", "p", null)]
    [Arguments("a/b/NAME", "a/b", "NAME")]
    public async Task SplitSource_SplitsOnLastSlash(string text, string pipeline, string? name)
    {
        var (p, n) = SecretCopyCommand.SplitSource(text);

        await Assert.That(p).IsEqualTo(pipeline);
        await Assert.That(n).IsEqualTo(name);
    }
}
