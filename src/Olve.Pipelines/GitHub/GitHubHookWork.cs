using System.Threading.Channels;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Triggers;

namespace Olve.Pipelines.GitHub;

/// <summary>A unit of GitHub-side work handed off from a (synchronous) trigger event handler.</summary>
public abstract record GitHubHookWork;

/// <summary>Register a <c>push</c> hook for a newly-added GitHub webhook trigger.</summary>
public record CreateHookWork(
    Id<Trigger> TriggerId,
    Id<Pipeline> PipelineId,
    string Owner,
    string Repo,
    string TokenSecretName,
    string HookSecret) : GitHubHookWork;

/// <summary>Delete the hook registered for a now-deleted trigger.</summary>
public record DeleteHookWork(
    Id<Trigger> TriggerId,
    Id<Pipeline> PipelineId,
    string Owner,
    string Repo,
    long HookId,
    string TokenSecretName) : GitHubHookWork;

/// <summary>
/// Single-consumer queue between the synchronous trigger event handlers and the background drainer.
/// Event handlers fire during reconcile and must not block on HTTP; they post here and return.
/// </summary>
public class GitHubHookWorkQueue
{
    private readonly Channel<GitHubHookWork> _channel = Channel.CreateUnbounded<GitHubHookWork>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelWriter<GitHubHookWork> Writer => _channel.Writer;
    public ChannelReader<GitHubHookWork> Reader => _channel.Reader;
}
