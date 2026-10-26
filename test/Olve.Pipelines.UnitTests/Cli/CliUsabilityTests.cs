using System.Net;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Commands.Dev;
using Olve.Pipelines.Cli.Commands.Secrets;

namespace Olve.Pipelines.UnitTests.Cli;

public class CliUsabilityTests
{
    // Catches: `pl --profile beta run list` printing the usage instead of running.
    [Test]
    [Arguments(new[] { "--profile", "beta", "run", "list", "p" }, new[] { "run", "list", "p", "--profile", "beta" })]
    [Arguments(new[] { "--json", "-v", "pipeline", "list" }, new[] { "pipeline", "list", "--json", "-v" })]
    [Arguments(new[] { "--api-url=https://x", "pipeline", "list" }, new[] { "pipeline", "list", "--api-url=https://x" })]
    [Arguments(new[] { "pipeline", "list", "--json" }, new[] { "pipeline", "list", "--json" })]
    [Arguments(new[] { "--nope", "pipeline" }, new[] { "--nope", "pipeline" })]
    [Arguments(new[] { "--help" }, new[] { "--help" })]
    public async Task GlobalsBeforeTheCommand_MoveAfterIt(string[] args, string[] expected) =>
        await Assert.That(CliDispatcher.GlobalsLast(args)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);

    // Catches: a script unable to tell "you called it wrong" (2) from "it failed" (1).
    [Test]
    [Arguments("no-such-command")]
    [Arguments("secret no-such-verb")]
    [Arguments("secret set only-one-argument")]
    [Arguments("secret set p N --no-such-option")]
    public async Task UsageErrors_Exit2(string args) =>
        await Assert.That(await new CliDispatcher(new ProcessRunner()).RunAsync(args.Split(' '))).IsEqualTo(2);

    [Test]
    public async Task AGlobalBeforeHelp_StillShowsHelp() =>
        await Assert.That(await new CliDispatcher(new ProcessRunner()).RunAsync(["--profile", "beta", "help"])).IsEqualTo(0);

    // Catches: `echo $TOKEN | pl secret set` silently storing "token\n", which breaks Authorization headers.
    [Test]
    [Arguments("token\n", true)]
    [Arguments("token\r\n", true)]
    [Arguments("token", false)]
    [Arguments("-----BEGIN KEY-----\nabc\n-----END KEY-----\n", false)]
    public async Task OneLineValueWithATrailingNewline_Warns(string value, bool warns) =>
        await Assert.That(SecretSetCommand.TrailingNewlineWarning(value) is not null).IsEqualTo(warns);

    [Test]
    public async Task DevFields_ReadFieldAndTheOlderInputSpelling()
    {
        CliArgs.Parse(["dev", "run", "x", "--field", "a=1", "--input", "b=2"], new HashSet<string>(StringComparer.Ordinal), CliCommand.NoAliases)
            .TryPickProblems(out _, out var cli);

        await Assert.That(DevCommandSupport.FieldPairs(cli!)).IsEquivalentTo(["a=1", "b=2"]);
    }

    // Catches: a network blip while the user approves in the browser throwing the whole device login away.
    [Test]
    public async Task DevicePoll_RidesOutNetworkErrors()
    {
        var calls = 0;
        using var http = new HttpClient(new Stub(() => ++calls switch
        {
            <= 2 => throw new HttpRequestException("connection reset"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"t","token_type":"Bearer"}""") },
        }));

        var token = await OidcClient.PollDeviceTokenAsync(http, "https://issuer.test/token", "c", "d", intervalSeconds: 1, CancellationToken.None);

        await Assert.That(TestRunDomain.Pick(token).AccessToken).IsEqualTo("t");
        await Assert.That(calls).IsEqualTo(3);
    }

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }
}
