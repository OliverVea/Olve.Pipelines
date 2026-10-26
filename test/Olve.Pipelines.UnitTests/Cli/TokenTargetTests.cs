using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands;

namespace Olve.Pipelines.UnitTests.Cli;

public class TokenTargetTests
{
    private static CliArgs NoFlags()
    {
        CliArgs.Parse(["pipeline"], new HashSet<string>(StringComparer.Ordinal), CliCommand.NoAliases).TryPickProblems(out _, out var cli);
        return cli!;
    }

    private static CliConfig LoggedIn(string? apiUrl) =>
        new() { Profile = "default", ApiUrl = apiUrl, Auth = new AuthConfig { AccessToken = "secret-token" } };

    // Catches: `pl --api-url https://elsewhere …` sending the profile's bearer token to another server.
    [Test]
    [Arguments("https://pipelines.example", "https://pipelines.example", true)]
    [Arguments("https://pipelines.example", "https://PIPELINES.example/", true)]
    [Arguments("https://pipelines.example", "https://elsewhere.example", false)]
    [Arguments("https://pipelines.example", "http://pipelines.example", false)]
    [Arguments("https://pipelines.example", "https://pipelines.example:8443", false)]
    [Arguments(null, ApiClientFactory.DefaultApiUrl, true)]
    [Arguments(null, "https://elsewhere.example", false)]
    public async Task ProfileToken_GoesOnlyToTheProfilesServer(string? profileUrl, string target, bool sent)
    {
        if (Environment.GetEnvironmentVariable(ApiClientFactory.TokenEnvVar) is { Length: > 0 })
            return; // an env token always wins; nothing to check here

        var (token, _) = ApiClientFactory.ResolveToken(NoFlags(), LoggedIn(profileUrl), target);

        await Assert.That(token).IsEqualTo(sent ? "secret-token" : null);
    }
}
