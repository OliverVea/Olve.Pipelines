using System.Net;
using System.Text;
using Olve.Results;
using Olve.Pipelines.Cli;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Commands;
using Olve.Pipelines.Cli.Diagnostics;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.UnitTests.Cli;

public class WatchCommandTests
{
    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    [Test]
    public async Task Transport_ParsesEventsAndMultilineData()
    {
        var transport = new ApiTransport(
            new HttpClient(new SseHandler("event: hello\ndata: {}\nretry: 5000\n\nevent: log\ndata: a\ndata: b\nid: 1-1\n\n"))
            {
                BaseAddress = new Uri("https://example.test"),
            },
            new StderrLog(false, TextWriter.Null));
        var events = new List<(string, string)>();

        var result = await transport.ReadEventsAsync("/api/events", (t, d) => { events.Add((t, d)); return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(events).IsEquivalentTo(new[] { ("hello", "{}"), ("log", "a\nb") });
    }

    [Test]
    public async Task Watch_PrintsEvents_SkipsKeepAlives()
    {
        var api = new FakePipelinesApi();
        api.WatchEventsScript.AddRange([
            ("hello", "{}"),
            ("ping", ""),
            ("attempt", """{"pipelineId":"p","run":3,"step":"build","attempt":1,"status":"in-progress"}"""),
        ]);
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
        CliArgs.Parse(["watch"], new HashSet<string>(StringComparer.Ordinal), CliCommand.NoAliases).TryPickProblems(out _, out var cli);
        cli!.OperandOffset = 1;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await new WatchCommand().Execute(cli, ctx, cts.Token);

        await Assert.That(result.Succeeded).IsTrue();
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("attempt").And.Contains("pipelineId=p run=3 step=build attempt=1 status=in-progress");
    }
}
