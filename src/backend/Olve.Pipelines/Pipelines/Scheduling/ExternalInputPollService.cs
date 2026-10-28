using System.Globalization;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Sources;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <param name="AllowedHosts">Hosts external-input polls may reach although they resolve to private addresses (<c>ExternalInputs:Poll:AllowedHosts</c>); empty = public only.</param>
public sealed record ExternalInputPollOptions(IReadOnlySet<string> AllowedHosts);

/// <summary>
/// The <c>poll</c> sources of waiting external inputs, as sources of the <see cref="PollScheduler"/>: while a step
/// waits, GET the input's URL every <c>intervalSeconds</c> and answer with the JSON at <c>valuePath</c> (<c>.</c> =
/// the whole body) once it validates against the input's model. Anything else (an error status, no value yet, a
/// value that doesn't validate) just means "not yet". Same network rules as poll triggers, plus
/// <see cref="ExternalInputPollOptions.AllowedHosts"/>.
/// </summary>
public sealed class ExternalInputPollService(
    AttemptService attempts,
    RunService runs,
    RunPlanCache plans,
    IServiceProvider sp,
    ExternalInputPollOptions options,
    ILogger<ExternalInputPollService> logger) : IPollSourceProvider
{
    private readonly HttpClient _http = PollHttp.Create(allowedHosts: options.AllowedHosts);

    public IEnumerable<PollSource> Sources()
    {
        var sources = new List<PollSource>();
        foreach (var attempt in attempts.List().Where(a => a.Status is Waiting))
        {
            if (!runs.TryGet(attempt.RunId, out var run)
                || plans.ForRun(run).TryPickProblems(out _, out var plan)
                || !plan.TryGetStep(attempt.StepName, out _, out var step))
                continue;

            foreach (var (name, input) in step.ExternalInputs)
            {
                if (attempt.Answers?.ContainsKey(name) == true || input.From.OfType<ExternalSource.Poll>().FirstOrDefault() is not { } poll)
                    continue;
                var url = poll.Url
                    .Replace("{run.id}", run.Id.Value.Value.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{run.number}", run.Number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                sources.Add(new PollSource(
                    $"input:{attempt.Id}:{name}",
                    TimeSpan.FromSeconds(poll.IntervalSeconds),
                    ct => PollOneAsync(run, attempt.StepName, name, url, poll.ValuePath, ct)));
            }
        }
        return sources;
    }

    private async Task PollOneAsync(Run run, string step, string input, string url, string valuePath, CancellationToken ct)
    {
        JsonElement value;
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Poll of input '{InputName}' of step '{StepName}' in run #{RunNumber}: {Url} answered {StatusCode}",
                    input, step, run.Number, url, (int)response.StatusCode);
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (At(doc.RootElement, valuePath) is not { } found)
                return;
            value = found.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Poll of input '{InputName}' of step '{StepName}' in run #{RunNumber} ('{RunId}') failed: {Error}",
                input, step, run.Number, run.Id, ex.Message);
            return;
        }

        using var scope = sp.CreateScope();
        var inputs = scope.ServiceProvider.GetRequiredService<ExternalInputService>();
        if (inputs.Answer(run.PipelineId, run.Number, step, input, value, new Answerer("poll", url)).TryPickProblems(out var problems))
            logger.LogDebug("Polled value for input '{InputName}' of step '{StepName}' in run #{RunNumber} not taken: {Reason}",
                input, step, run.Number, problems.First().FormattedMessage);
    }

    private static JsonElement? At(JsonElement root, string valuePath)
    {
        var current = root;
        foreach (var segment in valuePath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current;
    }
}
