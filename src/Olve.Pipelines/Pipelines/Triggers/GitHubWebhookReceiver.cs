using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>What the receiver decided to do with an inbound GitHub webhook delivery.</summary>
public enum GitHubWebhookAction
{
    /// <summary>No such trigger, or it is not a GitHub webhook trigger.</summary>
    NotFound,

    /// <summary>The HMAC signature did not match — reject (401).</summary>
    InvalidSignature,

    /// <summary>A delivery we deliberately do nothing about: ping, a non-push event, or a push to another branch.</summary>
    Ignore,

    /// <summary>A push to the configured branch — fire production.</summary>
    Fire,
}

/// <summary>
/// Pure decision logic for the GitHub webhook receiver, split out from the HTTP endpoint so the
/// signature/event/ref rules are unit-testable without spinning up the web host. The endpoint owns
/// reading the raw body and mapping the action to a status code; dispatch goes through
/// <see cref="TriggerExecutionService"/> as usual.
/// </summary>
public class GitHubWebhookReceiver(TriggerService triggers, ILogger<GitHubWebhookReceiver> logger)
{
    public GitHubWebhookAction Evaluate(
        Id<Trigger> triggerId, ReadOnlySpan<byte> body, string? signature, string? eventType)
    {
        if (triggers.TryGet(triggerId).TryPickProblems(out _, out var trigger)
            || trigger.Target is not GitHubWebhookTarget target)
            return GitHubWebhookAction.NotFound;

        if (!GitHubWebhookSignature.Verify(trigger.Secret, body, signature))
        {
            logger.LogWarning("GitHub webhook signature mismatch for trigger '{TriggerId}'", triggerId);
            return GitHubWebhookAction.InvalidSignature;
        }

        if (!string.Equals(eventType, "push", StringComparison.Ordinal))
        {
            // ping (the registration handshake) and every other event type.
            return GitHubWebhookAction.Ignore;
        }

        GitHubPushPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, GitHubWebhookJsonContext.Default.GitHubPushPayload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "GitHub webhook payload for trigger '{TriggerId}' was not valid JSON", triggerId);
            return GitHubWebhookAction.Ignore;
        }

        var expectedRef = $"refs/heads/{target.Branch}";
        if (!string.Equals(payload?.Ref, expectedRef, StringComparison.Ordinal))
        {
            logger.LogInformation("GitHub webhook for trigger '{TriggerId}' ignored ref '{Ref}' (want '{Expected}')",
                triggerId, payload?.Ref, expectedRef);
            return GitHubWebhookAction.Ignore;
        }

        return GitHubWebhookAction.Fire;
    }
}
