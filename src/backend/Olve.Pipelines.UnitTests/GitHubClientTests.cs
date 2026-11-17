using Microsoft.Extensions.Configuration;
using Olve.Pipelines.GitHub;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests;

/// <summary>Webhook registration goes to <c>GitHub:ApiUrl</c>, path prefix included (GitHub Enterprise serves /api/v3).</summary>
public class GitHubClientTests
{
    [Test]
    [Arguments("create", "POST /api/v3/repos/acme/widgets/hooks HTTP/1.1")]
    [Arguments("update", "PATCH /api/v3/repos/acme/widgets/hooks/7 HTTP/1.1")]
    [Arguments("delete", "DELETE /api/v3/repos/acme/widgets/hooks/7 HTTP/1.1")]
    public async Task Hook_requests_go_to_the_configured_api(string operation, string requestLine)
    {
        await using var server = LoopbackHttpServer.Start(LoopbackHttpServer.Response(200, """{"id":7}"""));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create<string, string?>("GitHub:ApiUrl", $"http://127.0.0.1:{server.Port}/api/v3/")])
            .Build();
        using var client = new GitHubClient(configuration);
        var config = new GitHubHookConfig("https://pipelines.example.com/api/webhooks/binding/b/github", "s");

        if (operation == "create")
            await Assert.That(await client.CreateHookAsync("acme", "widgets", "pat", config)).SucceededAndValue(id => id.IsEqualTo(7L));
        else
            await Assert.That(operation == "update"
                ? await client.UpdateHookAsync("acme", "widgets", "pat", 7, config)
                : await client.DeleteHookAsync("acme", "widgets", "pat", 7)).Succeeded();
        await Assert.That(server.Requests.Single()).StartsWith(requestLine + "\r\n");
    }
}
