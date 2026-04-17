using System.Text.Json;
using System.Text.RegularExpressions;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Polling;

public partial class PollTriggerService(
    EntityStore<Trigger> triggers,
    IServiceProvider sp,
    ILogger<PollTriggerService> logger) : BackgroundService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<Id<Trigger>, PollState> _states = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Poll trigger service started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in poll cycle");
            }

            await Task.Delay(LoopInterval, ct);
        }
    }

    private async Task PollCycleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var activeTriggers = GetPollTriggers();
        var activeIds = new HashSet<Id<Trigger>>();

        foreach (var (trigger, target) in activeTriggers)
        {
            activeIds.Add(trigger.Id);

            if (!_states.TryGetValue(trigger.Id, out var state))
            {
                state = new PollState(null, now);
                _states[trigger.Id] = state;
            }

            if (now < state.NextPoll)
                continue;

            _states[trigger.Id] = state with
            {
                NextPoll = now.AddSeconds(target.IntervalSeconds),
            };

            try
            {
                await PollTriggerAsync(trigger, target, state, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Poll failed for trigger '{TriggerId}' ({Url}): {ExceptionType}: {ExceptionMessage}. Will retry next interval",
                    trigger.Id, target.Url, ex.GetType().Name, ex.Message);
            }
        }

        // Remove state for deleted triggers
        var staleIds = _states.Keys.Where(id => !activeIds.Contains(id)).ToList();
        foreach (var id in staleIds)
            _states.Remove(id);
    }

    private async Task PollTriggerAsync(Trigger trigger, PollTriggerTarget target, PollState state, CancellationToken ct)
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, target.Url);

        if (target.Headers is not null)
        {
            var resolvedHeaders = await ResolveHeadersAsync(trigger.PipelineId, target.Headers, ct);
            foreach (var (key, value) in resolvedHeaders)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync(PollJsonContext.Default.JsonElement, ct);
        var currentValue = ExtractValue(json, target.ValuePath);

        if (state.LastValue is null)
        {
            logger.LogInformation("Poll trigger '{TriggerId}' initial value: {Value}", trigger.Id, currentValue);
            _states[trigger.Id] = _states[trigger.Id] with { LastValue = currentValue };
            return;
        }

        if (currentValue == state.LastValue)
            return;

        logger.LogInformation("Poll trigger '{TriggerId}' value changed: {OldValue} -> {NewValue}",
            trigger.Id, state.LastValue, currentValue);

        _states[trigger.Id] = _states[trigger.Id] with { LastValue = currentValue };

        using var scope = sp.CreateScope();
        var execution = scope.ServiceProvider.GetRequiredService<TriggerExecutionService>();
        var result = execution.ExecuteInternal(trigger.Id);

        if (result.TryPickProblems(out var problems))
            logger.LogWarning("Failed to execute poll trigger '{TriggerId}': {Problems}", trigger.Id, problems);
        else
            logger.LogInformation("Poll trigger '{TriggerId}' fired successfully", trigger.Id);
    }

    private async Task<Dictionary<string, string>> ResolveHeadersAsync(
        Id<Pipeline> pipelineId, Dictionary<string, string> headers, CancellationToken ct)
    {
        Dictionary<string, string>? secrets = null;
        var resolved = new Dictionary<string, string>(headers.Count);

        foreach (var (key, value) in headers)
        {
            if (!SecretPattern().IsMatch(value))
            {
                resolved[key] = value;
                continue;
            }

            secrets ??= await GetPipelineSecretsAsync(pipelineId, ct);

            resolved[key] = SecretPattern().Replace(value, match =>
            {
                var secretName = match.Groups[1].Value;
                if (secrets.TryGetValue(secretName, out var secretValue))
                    return secretValue;

                logger.LogWarning("Secret '{SecretName}' not found for pipeline '{PipelineId}'", secretName, pipelineId);
                return match.Value;
            });
        }

        return resolved;
    }

    private async Task<Dictionary<string, string>> GetPipelineSecretsAsync(Id<Pipeline> pipelineId, CancellationToken ct)
    {
        try
        {
            var kubernetesClient = sp.GetRequiredService<KubernetesClient>();
            var options = sp.GetRequiredService<KubernetesOptions>();
            var secretName = $"olve-pipeline-{pipelineId.Value.Value:N}";
            return await kubernetesClient.GetSecretAsync(options.Namespace, secretName, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch secrets for pipeline '{PipelineId}'", pipelineId);
            return [];
        }
    }

    private static string? ExtractValue(JsonElement element, string valuePath)
    {
        var parts = valuePath.Split('.');
        var current = element;

        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => current.GetRawText(),
        };
    }

    private List<(Trigger Trigger, PollTriggerTarget Target)> GetPollTriggers()
    {
        var results = new List<(Trigger, PollTriggerTarget)>();
        foreach (var trigger in triggers.List())
        {
            if (trigger.Target is PollTriggerTarget poll)
                results.Add((trigger, poll));
        }
        return results;
    }

    [GeneratedRegex(@"\$SECRET:(\w+)")]
    private static partial Regex SecretPattern();

    private record PollState(string? LastValue, DateTimeOffset NextPoll);
}
