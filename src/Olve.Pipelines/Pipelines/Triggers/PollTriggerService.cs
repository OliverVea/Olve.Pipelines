using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// Runs the <c>poll</c> triggers declared by each pipeline's current version (spec §3.6): every
/// <c>intervalSeconds</c> GET the URL (header values may reference <c>$SECRET:NAME</c>, resolved from the
/// pipeline's K8s Secret — only names the poll declares), extract the value at <c>valuePath</c>, and when it
/// changed run <c>map-trigger &lt;name&gt;</c> with <c>{"value": "…"}</c> and start a Poll run. The first
/// observation after a (re)start or a URL change only seeds the value. State is in memory: a restart costs
/// one seed, never a duplicate run (de-dup on the mapped input covers the overlap with push).
/// </summary>
public partial class PollTriggerService(
    EntityStore<Pipeline> pipelines,
    IServiceProvider sp,
    TimeProvider timeProvider,
    ILogger<PollTriggerService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private const long MinIntervalSeconds = 30;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed record PollState(string Url, DateTimeOffset NextAt, string? LastValue);

    private readonly Dictionary<(Id<Pipeline> Pipeline, string Trigger), PollState> _state = [];

    [GeneratedRegex(@"\$SECRET:([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex SecretRefRegex();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in the poll trigger loop");
            }

            await Task.Delay(Tick, timeProvider, ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var live = new HashSet<(Id<Pipeline>, string)>();
        foreach (var pipeline in pipelines.List())
        {
            using var scope = sp.CreateScope();
            var inputs = scope.ServiceProvider.GetRequiredService<RunInputService>();
            if (inputs.GetTarget(pipeline.Id).TryPickProblems(out _, out var target))
                continue;

            foreach (var poll in target.Manifest.Triggers.OfType<PollTrigger>())
            {
                var key = (pipeline.Id, poll.Name);
                live.Add(key);
                await PollOneAsync(scope.ServiceProvider, pipeline, poll, key, ct);
            }
        }

        foreach (var stale in _state.Keys.Where(k => !live.Contains(k)).ToList())
            _state.Remove(stale);
    }

    private async Task PollOneAsync(IServiceProvider services, Pipeline pipeline, PollTrigger poll, (Id<Pipeline>, string) key, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (_state.TryGetValue(key, out var state) && state.Url == poll.Url && now < state.NextAt)
            return;
        if (state is not null && state.Url != poll.Url)
            state = null; // a changed URL starts over (seed, don't trigger)

        var next = now + TimeSpan.FromSeconds(Math.Max(poll.IntervalSeconds, MinIntervalSeconds));
        var fetched = await FetchValueAsync(services, pipeline.Id, poll, ct);
        if (fetched.TryPickProblems(out var problems, out var value))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Poll trigger '{TriggerName}' of pipeline '{PipelineName}' ('{PipelineId}') failed",
                poll.Name, pipeline.Name, pipeline.Id);
            _state[key] = new PollState(poll.Url, next, state?.LastValue);
            return;
        }

        _state[key] = new PollState(poll.Url, next, value);
        if (state?.LastValue is null)
        {
            logger.LogInformation("Poll trigger '{TriggerName}' of pipeline '{PipelineName}' ('{PipelineId}') seeded at '{Value}'",
                poll.Name, pipeline.Name, pipeline.Id, value);
            return;
        }
        if (state.LastValue == value)
            return;

        logger.LogInformation("Poll trigger '{TriggerName}' of pipeline '{PipelineName}' ('{PipelineId}') changed '{Old}' -> '{New}'",
            poll.Name, pipeline.Name, pipeline.Id, state.LastValue, value);
        var outcome = await services.GetRequiredService<TriggerMappingService>().MapAndStartAsync(
            pipeline.Id, poll.Name, RunTriggerKind.Poll, new JsonObject { ["value"] = value }.ToJsonString(), ct);
        if (outcome.TryPickProblems(out var startProblems))
            logger.LogProblems(LogLevel.Warning, startProblems, "Poll trigger '{TriggerName}' of pipeline '{PipelineId}' did not start a run",
                poll.Name, pipeline.Id);
    }

    private static async Task<Result<string>> FetchValueAsync(IServiceProvider services, Id<Pipeline> pipelineId, PollTrigger poll, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, poll.Url);
        foreach (var (name, template) in poll.Headers)
        {
            var resolved = await ResolveSecretsAsync(services, pipelineId, poll, template, ct);
            if (resolved.TryPickProblems(out var problems, out var headerValue))
                return Result.Failure<string>(problems);
            request.Headers.TryAddWithoutValidation(name, headerValue);
        }
        if (!request.Headers.UserAgent.Any())
            request.Headers.TryAddWithoutValidation("User-Agent", "olve-pipelines");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ResultProblem("GET {0} failed: {1}", Redact(poll.Url), e.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return new ResultProblem("GET {0} returned {1}", Redact(poll.Url), (int)response.StatusCode);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException e)
            {
                return new ResultProblem("GET {0} did not return JSON: {1}", Redact(poll.Url), e.Message);
            }

            using (doc)
                return ExtractValue(doc.RootElement, poll.ValuePath);
        }
    }

    /// <summary>Resolves <c>$SECRET:NAME</c> references; only secrets the poll declares.</summary>
    private static async Task<Result<string>> ResolveSecretsAsync(
        IServiceProvider services, Id<Pipeline> pipelineId, PollTrigger poll, string template, CancellationToken ct)
    {
        var result = template;
        foreach (Match match in SecretRefRegex().Matches(template))
        {
            var name = match.Groups[1].Value;
            if (!poll.Secrets.Contains(name, StringComparer.Ordinal))
                return new ResultProblem("Header references secret '{0}', which poll '{1}' does not declare.", name, poll.Name);
            var value = await services.GetRequiredService<IPipelineSecretReader>().TryGetSecretAsync(pipelineId, name, ct);
            if (value.TryPickProblems(out var problems, out var secret))
                return Result.Failure<string>(problems);
            result = result.Replace(match.Value, secret, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Dot path (<c>a.b.0.c</c>; numeric segments index arrays). Strings are returned raw, other scalars as JSON text.</summary>
    public static Result<string> ExtractValue(JsonElement root, string valuePath)
    {
        var current = root;
        foreach (var segment in valuePath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
                current = child;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                     && index < current.GetArrayLength())
                current = current[index];
            else
                return new ResultProblem("valuePath '{0}' not found (at '{1}').", valuePath, segment);
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString()!,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
            _ => new ResultProblem("valuePath '{0}' is not a scalar ({1}).", valuePath, current.ValueKind),
        };
    }

    private static string Redact(string url) => url.Split('?')[0];
}
