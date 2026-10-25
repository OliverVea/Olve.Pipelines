using System.Text.Json;
using Olve.Pipelines.GitHub;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>What the binding webhook receiver decided to do with an inbound delivery.</summary>
public enum BindingWebhookAction
{
    /// <summary>No such binding, or it has no webhook secret (not a webhook-mode binding).</summary>
    NotFound,

    /// <summary>HMAC signature mismatch — reject (401).</summary>
    InvalidSignature,

    /// <summary>ping, a non-push event, or a push to another branch — acknowledge, do nothing.</summary>
    Ignore,

    /// <summary>A push to the bound branch — run reconcile+deploy.</summary>
    Deploy,
}

/// <summary>
/// Decision logic for the binding webhook receiver, split from the endpoint so the
/// signature/event/ref rules are unit-testable. Authenticates against the binding's
/// <see cref="PipelineConfigBinding.WebhookSecret"/> and, on a matching push, runs the binding's
/// reconcile-then-deploy cycle (config-before-build).
/// </summary>
/// <summary>The commit a push delivery announced, and when GitHub says it was pushed.</summary>
public record AnnouncedPush(string Sha, DateTimeOffset? PushedAt);

public class BindingWebhookReceiver(PipelineConfigBindingService bindings, ILogger<BindingWebhookReceiver> logger)
{
    public (BindingWebhookAction Action, Id<Pipeline> PipelineId, AnnouncedPush? Push) Evaluate(
        Id<PipelineConfigBinding> bindingId, ReadOnlySpan<byte> body, string? signature, string? eventType)
    {
        if (bindings.TryGet(bindingId).TryPickProblems(out _, out var binding)
            || string.IsNullOrEmpty(binding.WebhookSecret))
            return (BindingWebhookAction.NotFound, default, null);

        if (!GitHubWebhookSignature.Verify(binding.WebhookSecret, body, signature))
        {
            logger.LogWarning("Binding webhook signature mismatch for binding '{BindingId}'", bindingId);
            return (BindingWebhookAction.InvalidSignature, binding.PipelineId, null);
        }

        if (!string.Equals(eventType, "push", StringComparison.Ordinal))
            return (BindingWebhookAction.Ignore, binding.PipelineId, null);

        GitHubPushPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, GitHubWebhookJsonContext.Default.GitHubPushPayload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Binding webhook payload for binding '{BindingId}' was not valid JSON", bindingId);
            return (BindingWebhookAction.Ignore, binding.PipelineId, null);
        }

        var expectedRef = $"refs/heads/{binding.Branch}";
        if (!string.Equals(payload?.Ref, expectedRef, StringComparison.Ordinal))
        {
            logger.LogInformation("Binding webhook for '{BindingId}' ignored ref '{Ref}' (want '{Expected}')",
                bindingId, payload?.Ref, expectedRef);
            return (BindingWebhookAction.Ignore, binding.PipelineId, null);
        }

        var push = payload?.After is { Length: > 0 } after
            ? new AnnouncedPush(after, payload.Repository?.PushedAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null)
            : null;
        return (BindingWebhookAction.Deploy, binding.PipelineId, push);
    }
}
