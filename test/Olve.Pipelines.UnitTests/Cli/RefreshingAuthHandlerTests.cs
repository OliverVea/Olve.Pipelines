using System.Net;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Diagnostics;

namespace Olve.Pipelines.UnitTests.Cli;

public class RefreshingAuthHandlerTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<string?> Bearers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bearers.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Test]
    public async Task EachRequest_CarriesTheCurrentStoredToken()
    {
        var config = new CliConfig { Auth = new AuthConfig { AccessToken = "first", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } };
        var recorder = new Recorder();
        using var http = new HttpClient(new RefreshingAuthHandler(config, new StderrLog(false, TextWriter.Null), recorder))
        {
            BaseAddress = new Uri("http://api.test"),
        };

        await http.GetAsync("/a");
        config.Auth.AccessToken = "second"; // as a refresh would
        await http.GetAsync("/b");

        await Assert.That(string.Join(",", recorder.Bearers)).IsEqualTo("first,second");
    }
}
