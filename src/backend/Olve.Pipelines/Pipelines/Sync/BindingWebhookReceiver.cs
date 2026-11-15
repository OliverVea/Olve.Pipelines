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

    /// <summary>ping, another event, or a push to another branch — acknowledge, do nothing.</summary>
    Ignore,

    /// <summary>A push to the bound branch — run reconcile+deploy.</summary>
    Deploy,

    /// <summary>A <c>pull_request</c> delivery: GitHub's view of the pull request, for <see cref="Ci.PullRequestService"/>.</summary>
    PullRequest,
}

/// <summary>What the receiver decided, with the push it announced or the pull request it reported.</summary>
public sealed record BindingWebhookDecision(
    BindingWebhookAction Action, Id<Pipeline> PipelineId, AnnouncedPush? Push, GitHubPullRequest? PullRequest = null, string? Branch = null)
{
    public void Deconstruct(out BindingWebhookAction action, out Id<Pipeline> pipelineId, out AnnouncedPush? push)
        => (action, pipelineId, push) = (Action, PipelineId, Push);
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
    public BindingWebhookDecision Evaluate(
        Id<PipelineConfigBinding> bindingId, ReadOnlySpan<byte> body, string? signature, string? eventType)
    {
        if (bindings.TryGet(bindingId).TryPickProblems(out _, out var binding)
            || string.IsNullOrEmpty(binding.WebhookSecret))
            return new(BindingWebhookAction.NotFound, default, null);

        if (!GitHubWebhookSignature.Verify(binding.WebhookSecret, body, signature))
        {
            logger.LogWarning("Binding webhook signature mismatch for binding '{BindingId}'", bindingId);
            return new(BindingWebhookAction.InvalidSignature, binding.PipelineId, null);
        }

        if (string.Equals(eventType, "pull_request", StringComparison.Ordinal))
            return EvaluatePullRequest(binding, body);
        if (!string.Equals(eventType, "push", StringComparison.Ordinal))
            return new(BindingWebhookAction.Ignore, binding.PipelineId, null);

        GitHubPushPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, GitHubWebhookJsonContext.Default.GitHubPushPayload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Binding webhook payload for binding '{BindingId}' was not valid JSON", bindingId);
            return new(BindingWebhookAction.Ignore, binding.PipelineId, null);
        }

        var expectedRef = $"refs/heads/{binding.Branch}";
        if (!string.Equals(payload?.Ref, expectedRef, StringComparison.Ordinal))
        {
            logger.LogInformation("Binding webhook for '{BindingId}' ignored ref '{Ref}' (want '{Expected}')",
                bindingId, payload?.Ref, expectedRef);
            return new(BindingWebhookAction.Ignore, binding.PipelineId, null);
        }

        var push = payload?.After is { Length: > 0 } after
            ? new AnnouncedPush(after, payload.Repository?.PushedAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null)
            : null;
        return new(BindingWebhookAction.Deploy, binding.PipelineId, push);
    }

    /// <summary>Every action carries the whole pull request, so each is applied the same way; base, fork and age are checked when it is.</summary>
    private BindingWebhookDecision EvaluatePullRequest(PipelineConfigBinding binding, ReadOnlySpan<byte> body)
    {
        GitHubPullRequestEvent? delivery;
        try
        {
            delivery = JsonSerializer.Deserialize(body, GitHubPullRequestJsonContext.Default.GitHubPullRequestEvent);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Binding webhook pull_request payload for binding '{BindingId}' was not valid JSON", binding.Id);
            return new(BindingWebhookAction.Ignore, binding.PipelineId, null);
        }

        return delivery?.PullRequest is { } pull
            ? new(BindingWebhookAction.PullRequest, binding.PipelineId, null, pull, binding.Branch)
            : new(BindingWebhookAction.Ignore, binding.PipelineId, null);
    }
}
