using System.Text.Json;
using Olve.Pipelines.Cli.Api;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands;

/// <summary><c>pl watch [&lt;pipeline&gt;]</c> — prints live change events (<c>GET /api/events</c>) until Ctrl+C.</summary>
public sealed class WatchCommand : ICliCommand
{
    public string Noun => "watch";
    public string Verb => "";
    public string HelpLine => "Print live change events (runs, attempts, versions, blocked steps, bindings) until Ctrl+C";
    public string? HelpDetail =>
        """
        pl watch [<pipeline>] [--json]

          Streams the instance's change notifications (all pipelines, or one). Each line is
          `<time> <event> <fields>`; --json prints {"event": ..., "data": ...} per line.
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        string? pipelineId = null;
        if (cli.Operand(0) is { } text)
        {
            if ((await ResolvePipelineAsync(ctx, text, ct)).TryPickProblems(out var problems, out var pipeline))
                return problems;
            pipelineId = pipeline.Id.ToString();
        }

        var result = await ctx.Api.WatchEvents(pipelineId, (type, data) =>
        {
            if (type is "ping" or "hello") return Task.CompletedTask;
            ctx.Output.Line(ctx.Json
                ? $"{{\"event\":{JsonSerializer.Serialize(type, CliJsonContext.Default.String)},\"data\":{(data.Length == 0 ? "null" : data)}}}"
                : $"{DateTime.Now:HH:mm:ss}  {type,-8}  {Describe(type, data, omitPipeline: pipelineId is not null)}");
            return Task.CompletedTask;
        }, ct);

        if (result.TryPickProblems(out var watchProblems))
            return watchProblems;
        if (!ct.IsCancellationRequested)
            Console.Error.WriteLine("The event stream ended (the service restarted?).");
        return Result.Success();
    }

    private static string Describe(string type, string data, bool omitPipeline)
    {
        if (type == "resync") return "missed events: refetch what you are looking at";
        try
        {
            using var doc = JsonDocument.Parse(data);
            var fields = doc.RootElement.EnumerateObject()
                .Where(p => !(omitPipeline && p.Name == "pipelineId"))
                .Select(p => $"{p.Name}={(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}");
            return string.Join(' ', fields);
        }
        catch (JsonException)
        {
            return data;
        }
    }
}
