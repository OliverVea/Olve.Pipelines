using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Sources;

namespace Olve.Pipelines.Pipelines.Triggers;

/// <summary>
/// Push detection for bound pipelines whose current version declares <c>onPush</c>: a
/// <see cref="PollScheduler"/> source per such binding (every <see cref="ReconcileOptions.PollInterval"/>),
/// nudged by the binding's GitHub webhook (<see cref="RequestCheck"/>). Each check reads the branch head,
/// then runs through <see cref="IConfigLock"/> (the config commit is applied first), compares that head
/// with <see cref="PipelineConfigBinding.LastPushSha"/> and fires the push trigger when it advanced. The
/// head is read before the config, so a commit pushed while the config builds waits for the next check
/// instead of starting on a version without its config. The first observation only seeds the cursor.
/// Hosted only so shutdown cancels requested checks.
/// </summary>
/// <summary>A webhook delivery's arrival and the push it announced.</summary>
public record PushNotice(DateTimeOffset ReceivedAt, AnnouncedPush? Push);

public class PushTriggerService(
    EntityStore<PipelineConfigBinding> bindings,
    ReconcileOptions options,
    IServiceProvider sp,
    TimeProvider time,
    ILogger<PushTriggerService> logger) : IPollSourceProvider, IHostedService, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    public IEnumerable<PollSource> Sources()
    {
        using var scope = sp.CreateScope();
        var inputs = scope.ServiceProvider.GetRequiredService<RunInputService>();
        return bindings.List()
            .Where(b => b.Polls && !inputs.GetTarget(b.PipelineId).TryPickProblems(out _, out var target) && target.Manifest.Triggers.OfType<PushTrigger>().Any())
            .Select(b => new PollSource($"push:{b.Id}", options.PollInterval, async ct =>
            {
                try
                {
                    await CheckAsync(b.PipelineId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Push check failed for '{Repo}@{Branch}' (pipeline '{PipelineId}')", b.Repo, b.Branch, b.PipelineId);
                }
            }))
            .ToList();
    }

    /// <summary>Applies the pipeline's config (<paramref name="forceConfig"/>: also retry a failed commit), then checks the branch head.
    /// <paramref name="notice"/>: the webhook delivery that asked for the check (null: a poll).</summary>
    public async Task CheckAsync(Id<Pipeline> pipelineId, CancellationToken ct, bool forceConfig = false, PushNotice? notice = null)
    {
        var noticedAt = notice?.ReceivedAt ?? time.GetUtcNow();
        using var scope = sp.CreateScope();
        var services = scope.ServiceProvider;
        if (services.GetRequiredService<PipelineConfigBindingService>().GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding))
            return;

        var headResult = await services.GetRequiredService<IConfigSource>().GetBranchHeadShaAsync(binding, ct);
        if (headResult.TryPickProblems(out var problems, out var head))
        {
            logger.LogProblems(LogLevel.Warning, problems, "Push check: could not read branch head for '{Repo}@{Branch}'", binding.Repo, binding.Branch);
            return;
        }

        var origin = new RunOrigin(noticedAt, notice is null ? "poll" : "webhook",
            PushedAt: notice?.Push is { } push && push.Sha == head ? push.PushedAt : null);
        await services.GetRequiredService<IConfigLock>().RunIfConfigCurrentAsync(pipelineId,
            () => DetectAsync(services, pipelineId, head, origin with { ConfigReadyAt = time.GetUtcNow() }, ct), ct, forceConfig);
    }

    /// <summary>Starts <see cref="CheckAsync"/> in the background (a webhook delivery must not wait for a build).</summary>
    public Result RequestCheck(Id<Pipeline> pipelineId, PushNotice? notice = null)
    {
        if (bindings.List().All(b => b.PipelineId != pipelineId))
            return new NotFoundProblem("Pipeline '{0}' has no config binding.", pipelineId);

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(pipelineId, _lifetime.Token, notice: notice);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Push check of pipeline '{PipelineId}' failed", pipelineId);
            }
        });
        return Result.Success();
    }

    private async Task DetectAsync(IServiceProvider services, Id<Pipeline> pipelineId, string head, RunOrigin origin, CancellationToken ct)
    {
        var bindingService = services.GetRequiredService<PipelineConfigBindingService>();
        if (bindingService.GetByPipelineId(pipelineId).TryPickProblems(out _, out var binding))
            return;

        if (binding.LastPushSha == head)
            return;

        // First observation seeds the cursor — restarts and freshly bound pipelines never treat
        // already-current code as a new push.
        if (binding.LastPushSha is null)
        {
            logger.LogInformation("Push check: seeding cursor for '{Repo}@{Branch}' at {Sha}", binding.Repo, binding.Branch, head);
            LogIfFailed(bindingService.SetLastPushSha(binding.Id, head), binding);
            return;
        }

        logger.LogInformation("Push check: '{Repo}@{Branch}' advanced {Old} -> {New}; firing the push trigger",
            binding.Repo, binding.Branch, binding.LastPushSha, head);

        // The cursor moves only once the trigger fired, so a failed fire (mapping Job, K8s, a restart)
        // is retried on the next check; de-dup makes a repeat harmless.
        var fired = await services.GetRequiredService<IPushTriggerHandler>().FireAsync(binding, head, ct, origin);
        if (fired.TryPickProblems(out var fireProblems))
        {
            logger.LogProblems(LogLevel.Warning, fireProblems,
                "Push trigger of pipeline '{PipelineId}' for {Sha} did not start a run; retrying on the next check", binding.PipelineId, head);
            return;
        }

        LogIfFailed(bindingService.SetLastPushSha(binding.Id, head), binding);
    }

    private void LogIfFailed(Result<PipelineConfigBinding> result, PipelineConfigBinding binding)
    {
        if (result.TryPickProblems(out var problems))
            logger.LogProblems(LogLevel.Warning, problems, "Push check: could not record the push cursor of '{Repo}@{Branch}'", binding.Repo, binding.Branch);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _lifetime.CancelAsync();

    public void Dispose()
    {
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
