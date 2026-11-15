using System.Threading.Channels;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Ci;

/// <summary>
/// Brings a binding's tracked pull requests level with GitHub, for deliveries the webhook missed: every open pull
/// request into the branch is observed, and one tracked as open that GitHub no longer lists is read on its own.
/// </summary>
public class PullRequestCatchUp(IPullRequestSource source, PullRequestService pulls, TimeProvider time)
{
    public async Task<Result> CatchUpAsync(PipelineConfigBinding binding, CancellationToken ct)
    {
        if ((await source.ListOpenAsync(binding, ct)).TryPickProblems(out var problems, out var open))
            return problems;

        var now = time.GetUtcNow();
        foreach (var reported in open)
            pulls.Observe(binding.PipelineId, binding.Branch, reported, now);

        var listed = open.Select(p => p.Number).ToHashSet();
        foreach (var held in pulls.ListByPipeline(binding.PipelineId).Where(p => p.State == PullRequestState.Open && !listed.Contains(p.Number)))
        {
            if ((await source.GetAsync(binding, held.Number, ct)).TryPickProblems(out problems, out var reported))
                return problems;
            if (reported is null)
                pulls.Forget(binding.PipelineId, held.Number);
            else
                pulls.Observe(binding.PipelineId, binding.Branch, reported, now);
        }

        return Result.Success();
    }
}

/// <summary>Bindings whose pull requests should be caught up, drained by <see cref="PullRequestCatchUpService"/>.</summary>
public class PullRequestCatchUpQueue
{
    private readonly Channel<Id<PipelineConfigBinding>> _channel = Channel.CreateUnbounded<Id<PipelineConfigBinding>>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Request(Id<PipelineConfigBinding> bindingId) => _channel.Writer.TryWrite(bindingId);

    public ChannelReader<Id<PipelineConfigBinding>> Reader => _channel.Reader;
}

/// <summary>Catches up every binding at startup, a new binding, one whose branch changed, and one whose hook was (re)registered.</summary>
public class PullRequestCatchUpRegistration(
    EntityStore<PipelineConfigBinding> bindings,
    PipelineConfigBindingEvents bindingEvents,
    BindingHookStateStore hooks,
    PullRequestCatchUpQueue queue) : IRunOnStartup
{
    public Result Run()
    {
        bindingEvents.OnAdded.Subscribe(e => queue.Request(e.Id));
        bindingEvents.OnUpdated.Subscribe(e =>
        {
            if (e.Before.Branch != e.After.Branch || e.Before.Repo != e.After.Repo)
                queue.Request(e.Id);
        });
        hooks.OnSet.Subscribe(queue.Request);
        foreach (var binding in bindings.List().ToList())
            queue.Request(binding.Id);
        return Result.Success();
    }
}

public class PullRequestCatchUpService(
    PullRequestCatchUpQueue queue,
    EntityStore<PipelineConfigBinding> bindings,
    IServiceProvider sp,
    ILogger<PullRequestCatchUpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var bindingId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!bindings.TryGet(bindingId, out var binding))
                continue;
            try
            {
                var result = await sp.GetRequiredService<PullRequestCatchUp>().CatchUpAsync(binding, stoppingToken);
                if (result.TryPickProblems(out var problems))
                    logger.LogProblems(LogLevel.Warning, problems, "Could not catch up the pull requests of '{Repo}@{Branch}'", binding.Repo, binding.Branch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Catching up the pull requests of '{Repo}@{Branch}' failed", binding.Repo, binding.Branch);
            }
        }
    }
}
