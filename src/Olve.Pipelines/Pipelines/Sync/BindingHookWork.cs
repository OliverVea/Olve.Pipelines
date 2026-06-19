using System.Threading.Channels;

namespace Olve.Pipelines.Pipelines.Sync;

/// <summary>A unit of binding-hook work handed off from the (synchronous) binding event handler.</summary>
public abstract record BindingHookWork;

/// <summary>Register a push hook for a binding that entered (or is in) a webhook mode.</summary>
public record CreateBindingHookWork(
    Id<PipelineConfigBinding> BindingId,
    Id<Pipeline> PipelineId,
    string Owner,
    string Repo,
    string CredentialsSecret,
    string HookSecret) : BindingHookWork;

/// <summary>Delete the hook registered for a binding (deleted, or switched to poll mode).</summary>
public record DeleteBindingHookWork(
    Id<PipelineConfigBinding> BindingId,
    Id<Pipeline> PipelineId,
    string Owner,
    string Repo,
    long HookId,
    string CredentialsSecret) : BindingHookWork;

/// <summary>Single-consumer queue between the synchronous binding event handlers and the drainer.</summary>
public class BindingHookWorkQueue
{
    private readonly Channel<BindingHookWork> _channel = Channel.CreateUnbounded<BindingHookWork>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelWriter<BindingHookWork> Writer => _channel.Writer;
    public ChannelReader<BindingHookWork> Reader => _channel.Reader;
}
