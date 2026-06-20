using System.Net;
using Olve.Results;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Diagnostics;

namespace Olve.Pipelines.UnitTests.Cli;

public class ApiTransportTests
{
    /// <summary>Answers requests from a scripted responder; records the path hit.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(responder(request));
        }
    }

    private static ApiTransport Transport(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test") }, new StderrLog(false, TextWriter.Null));

    [Test]
    public async Task Get_Success_DeserializesBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"id":"11111111-1111-1111-1111-111111111111","name":"alpha"}]"""),
        });

        var result = await Transport(handler).GetAsync("/api/pipelines/", CliJsonContext.Default.PipelineArray, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        result.TryPickProblems(out _, out var pipelines);
        await Assert.That(pipelines!.Length).IsEqualTo(1);
        await Assert.That(pipelines[0].Name).IsEqualTo("alpha");
        await Assert.That(handler.Paths).Contains("/api/pipelines/");
    }

    [Test]
    public async Task Get_Unauthorized_MapsToLoginProblem()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await Transport(handler).GetAsync("/api/pipelines/", CliJsonContext.Default.PipelineArray, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        result.TryPickProblems(out var problems);
        await Assert.That(problems?.Any(p => p.ToString()!.Contains("pl login")) ?? false).IsTrue();
    }

    [Test]
    public async Task Get_ServerError_IncludesStatusAndBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });

        var result = await Transport(handler).GetAsync("/api/pipelines/", CliJsonContext.Default.PipelineArray, CancellationToken.None);

        await Assert.That(result.Failed).IsTrue();
        result.TryPickProblems(out var problems);
        await Assert.That(problems?.Any(p => p.ToString()!.Contains("500")) ?? false).IsTrue();
        await Assert.That(problems?.Any(p => p.ToString()!.Contains("boom")) ?? false).IsTrue();
    }
}
